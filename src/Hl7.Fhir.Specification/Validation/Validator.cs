﻿/* 
 * Copyright (c) 2016, Furore (info@furore.com) and contributors
 * See the file CONTRIBUTORS for details.
 * 
 * This file is licensed under the BSD 3-Clause license
 * available at https://raw.githubusercontent.com/ewoutkramer/fhir-net-api/master/LICENSE
 */

using Hl7.ElementModel;
using Hl7.Fhir.FluentPath;
using Hl7.Fhir.Model;
using Hl7.Fhir.Specification.Navigation;
using Hl7.Fhir.Specification.Snapshot;
using Hl7.Fhir.Specification.Source;
using Hl7.Fhir.Specification.Terminology;
using Hl7.Fhir.Support;
using Hl7.FluentPath;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using Time = Hl7.FluentPath.Time;

namespace Hl7.Fhir.Validation
{
    public class Validator
    {
        public ValidationSettings Settings { get; private set; }

        public event EventHandler<OnSnapshotNeededEventArgs> OnSnapshotNeeded;
        public event EventHandler<OnResolveResourceReferenceEventArgs> OnExternalResolutionNeeded;

        internal ScopeTracker ScopeTracker = new ScopeTracker();  
        public Validator(ValidationSettings settings)
        {
            Settings = settings;
        }

        public Validator() : this(ValidationSettings.Default)
        {
        }

        public OperationOutcome Validate(IElementNavigator instance)
        {
            return Validate(instance, declaredTypeProfile: null, statedCanonicals: null, statedProfiles: null);
        }

        public OperationOutcome Validate(IElementNavigator instance, params string[] definitionUris)
        {
            return Validate(instance, (IEnumerable<string>)definitionUris);
        }

        public OperationOutcome Validate(IElementNavigator instance, IEnumerable<string> definitionUris)
        {
            return Validate(instance, declaredTypeProfile: null, statedCanonicals: definitionUris, statedProfiles: null);
        }

        public OperationOutcome Validate(IElementNavigator instance, params StructureDefinition[] structureDefinitions)
        {
            return Validate(instance, (IEnumerable<StructureDefinition>) structureDefinitions );
        }

        public OperationOutcome Validate(IElementNavigator instance, IEnumerable<StructureDefinition> structureDefinitions)
        {
            return Validate(instance, declaredTypeProfile: null, statedCanonicals: null, statedProfiles: structureDefinitions);
        }


        // This is the one and only main entry point for all external validation calls (i.e. invoked by the user of the API)
        internal OperationOutcome Validate(IElementNavigator instance, string declaredTypeProfile, IEnumerable<string> statedCanonicals, IEnumerable<StructureDefinition> statedProfiles)
        {
            var processor = new ProfilePreprocessor(profileResolutionNeeded, snapshotGenerationNeeded, instance, declaredTypeProfile, statedProfiles, statedCanonicals);
            var outcome = processor.Process();

            // Note: only start validating if the profiles are complete and consistent
            if(outcome.Success)
                outcome.Add(Validate(instance, processor.Result));

            return outcome;
        }


        internal OperationOutcome Validate(IElementNavigator instance, ElementDefinitionNavigator definition)
        {
            return Validate(instance, new[] { definition });
        }


        // This is the one and only main internal entry point for all validations
        internal OperationOutcome Validate(IElementNavigator instance, IEnumerable<ElementDefinitionNavigator> definitions)
        {
            var outcome = new OperationOutcome();

            try
            {
                List<ElementDefinitionNavigator> allDefinitions = new List<ElementDefinitionNavigator>(definitions);

                if (allDefinitions.Count() == 1)
                    outcome.Add(validateElement(allDefinitions.Single(), instance));
                else
                {
                    var validators = allDefinitions.Select(nav => createValidator(nav, instance));
                    outcome.Add(this.Combine(BatchValidationMode.All, instance, validators));
                }
            }
            catch (Exception e)
            {
                outcome.AddIssue($"Internal logic failure: {e.Message}", Issue.PROCESSING_CATASTROPHIC_FAILURE, instance);
            }

            return outcome;
        }


        private Func<OperationOutcome> createValidator(ElementDefinitionNavigator nav, IElementNavigator instance)
        {
            return () => validateElement(nav, instance);
        }


        private OperationOutcome validateElement(ElementDefinitionNavigator definition, IElementNavigator instance)
        {
            var outcome = new OperationOutcome();

            try
            {
                Trace(outcome, "Start validation of ElementDefinition at path '{0}'".FormatWith(definition.QualifiedDefinitionPath()), Issue.PROCESSING_PROGRESS, instance);

                ScopeTracker.Enter(instance);

                // If navigator cannot be moved to content, there's really nothing to validate against.
                if (definition.AtRoot && !definition.MoveToFirstChild())
                {
                    outcome.AddIssue($"Snapshot component of profile '{definition.StructureDefinition?.Url}' has no content.", Issue.PROFILE_ELEMENTDEF_IS_EMPTY, instance);
                    return outcome;
                }

                // Any node must either have a value, or children, or both (e.g. extensions on primitives)
                if (instance.Value == null && !instance.HasChildren())
                {
                    outcome.AddIssue("Element must not be empty", Issue.CONTENT_ELEMENT_MUST_HAVE_VALUE_OR_CHILDREN, instance);
                    return outcome;
                }

                var elementConstraints = definition.Current;

                if (elementConstraints.IsPrimitiveValueConstraint())
                {
                    // The "value" property of a FHIR Primitive is the bottom of our recursion chain, it does not have a nameReference
                    // nor a <type>, the only thing left to do to validate the content is to validate the string representation of the
                    // primitive against the regex given in the core definition
                    outcome.Add(VerifyPrimitiveContents(elementConstraints, instance));
                }
                else if (definition.HasChildren)
                {
                    // Handle in-lined constraints on children. In a snapshot, these children should be exhaustive,
                    // so there's no point in also validating the <type> or <nameReference>
                    // TODO: Check whether this is even true when the <type> has a profile?
                    outcome.Add(this.ValidateChildConstraints(definition, instance));
                }
                else
                {
                    // No inline-children, so validation depends on the presence of a <type> or <nameReference>
                    if (elementConstraints.Type != null || elementConstraints.NameReference != null)

                    {
                        outcome.Add(this.ValidateType(elementConstraints, instance));
                        outcome.Add(ValidateNameReference(elementConstraints, definition, instance));
                    }
                    else
                        Trace(outcome, "ElementDefinition has no child, nor does it specify a type or nameReference to validate the instance data against", Issue.PROFILE_ELEMENTDEF_CONTAINS_NO_TYPE_OR_NAMEREF, instance);
                }

                outcome.Add(ValidateSlices(definition, instance));

                outcome.Add(this.ValidateFixed(elementConstraints, instance));
                outcome.Add(this.ValidatePattern(elementConstraints, instance));
                outcome.Add(this.ValidateMinMaxValue(elementConstraints, instance));
                outcome.Add(ValidateMaxLength(elementConstraints, instance));
                outcome.Add(ValidateConstraints(elementConstraints, instance));
                outcome.Add(this.ValidateBinding(elementConstraints, instance));

                // If the report only has partial information, no use to show the hierarchy, so flatten it.
                if (Settings.Trace == false) outcome.Flatten();

                return outcome;
            }
            finally
            {
                ScopeTracker.Leave(instance);
            }
        }


        internal OperationOutcome ValidateConstraints(ElementDefinition definition, IElementNavigator instance)
        {
            var outcome = new OperationOutcome();

            if (Settings.SkipConstraintValidation) return outcome;

            var context = ScopeTracker.ResourceContext(instance);

            // <constraint>
            //  <extension url="http://hl7.org/fhir/StructureDefinition/structuredefinition-expression">
            //    <valueString value="reference.startsWith('#').not() or (reference.substring(1).trace('url') in %resource.contained.id.trace('ids'))"/>
            //  </extension>
            //  <key value="ref-1"/>
            //  <severity value="error"/>
            //  <human value="SHALL have a local reference if the resource is provided inline"/>
            //  <xpath value="not(starts-with(f:reference/@value, &#39;#&#39;)) or exists(ancestor::*[self::f:entry or self::f:parameter]/f:resource/f:*/f:contained/f:*[f:id/@value=substring-after(current()/f:reference/@value, &#39;#&#39;)]|/*/f:contained/f:*[f:id/@value=substring-after(current()/f:reference/@value, &#39;#&#39;)])"/>
            //</constraint>
            // 

            foreach (var constraintElement in definition.Constraint)
            {
                var fpExpression = constraintElement.GetFluentPathConstraint();

                if (fpExpression != null)
                {
                    try
                    {
                        bool success = instance.Predicate(fpExpression, context);

                        if (!success)
                        {
                            var text = "Instance failed constraint " + constraintElement.ConstraintDescription();
                            var issue = constraintElement.Severity == ElementDefinition.ConstraintSeverity.Error ?
                                Issue.CONTENT_ELEMENT_FAILS_ERROR_CONSTRAINT : Issue.CONTENT_ELEMENT_FAILS_WARNING_CONSTRAINT;

                            Trace(outcome, text, issue, instance);
                        }

                    }
                    catch (Exception e)
                    {
                        Trace(outcome, $"Evaluation of FluentPath for constraint '{constraintElement.Key}' failed: {e.Message}",
                                        Issue.PROFILE_ELEMENTDEF_INVALID_FLUENTPATH_EXPRESSION, instance);
                    }
                }
                else
                    Trace(outcome, $"Encountered an invariant ({constraintElement.Key}) that has no FluentPath expression, skipping validation of this constraint",
                                Issue.UNSUPPORTED_CONSTRAINT_WITHOUT_FLUENTPATH, instance);
            }

            return outcome;
        }

        internal OperationOutcome ValidateSlices(ElementDefinitionNavigator definition, IElementNavigator instance)
        {
            var outcome = new OperationOutcome();

            if (definition.Current.Slicing != null)
            {
                // This is the slicing entry
                // TODO: Find my siblings and try to validate the content against
                // them. There should be exactly one slice validating against the
                // content, otherwise the slicing is ambiguous. If there's no match
                // we fail validation as well. 
                // For now, we do not handle slices
                if(definition.Current.Slicing != null)
                    Trace(outcome, "ElementDefinition uses slicing, which is not yet supported. Instance has not been validated against " +
                            "any of the slices", Issue.UNAVAILABLE_REFERENCED_PROFILE, instance);
            }

            return outcome;
        }

        internal OperationOutcome ValidateBinding(ElementDefinition definition, IElementNavigator instance)
        {
            var outcome = new OperationOutcome();
            var ts = Settings.TerminologyService;

            if (ts == null)
            {
                if (Settings.ResourceResolver == null)
                {
                    Trace(outcome, $"Cannot resolve binding references since neither TerminologyService nor ResourceResolver is given in the settings",
                        Issue.UNAVAILABLE_TERMINOLOGY_SERVER, instance);
                    return outcome;
                }

                ts = new LocalTerminologyServer(Settings.ResourceResolver);
            }

            var bindingValidator = new BindingValidator(ts, instance.Path);

            try
            {
                return bindingValidator.ValidateBinding(instance, definition);
            }
            catch (Exception e)
            {
                Trace(outcome, $"Terminology service failed while validating code X (system Y): {e.Message}", Issue.UNAVAILABLE_VALIDATE_CODE_FAILED, instance);
                return outcome;
            }
        }


        internal static FHIRDefinedType? DetermineType(ElementDefinition definition, IElementNavigator instance)
        {
            if (definition.IsChoice())
            {
                if (instance.TypeName != null)
                    return ModelInfo.FhirTypeNameToFhirType(instance.TypeName);
                else
                    return null;
            }
            else
                return definition.Type.First().Code.Value;
        }
  

        internal OperationOutcome ValidateNameReference(ElementDefinition definition, ElementDefinitionNavigator allDefinitions, IElementNavigator instance)
        {
            var outcome = new OperationOutcome();

            if (definition.NameReference != null)
            {
                Trace(outcome, "Start validation of constraints referred to by nameReference '{0}'".FormatWith(definition.NameReference), Issue.PROCESSING_PROGRESS, instance);

                var referencedPositionNav = allDefinitions.ShallowCopy();

                if (referencedPositionNav.JumpToNameReference(definition.NameReference))
                    outcome.Include(Validate(instance, referencedPositionNav));
                else
                    Trace(outcome, $"ElementDefinition uses a non-existing nameReference '{definition.NameReference}'", Issue.PROFILE_ELEMENTDEF_INVALID_NAMEREFERENCE, instance);

            }

            return outcome;
        }
        
        internal OperationOutcome VerifyPrimitiveContents(ElementDefinition definition, IElementNavigator instance)
        {
            var outcome = new OperationOutcome();

            Trace(outcome, "Verifying content of the leaf primitive value attribute", Issue.PROCESSING_PROGRESS, instance);

            // Go look for the primitive type extensions
            //  <extension url="http://hl7.org/fhir/StructureDefinition/structuredefinition-regex">
            //        <valueString value="-?([0]|([1-9][0-9]*))"/>
            //      </extension>
            //      <code>
            //        <extension url="http://hl7.org/fhir/StructureDefinition/structuredefinition-json-type">
            //          <valueString value="number"/>
            //        </extension>
            //        <extension url="http://hl7.org/fhir/StructureDefinition/structuredefinition-xml-type">
            //          <valueString value="int"/>
            //        </extension>
            //      </code>
            // Note that the implementer of IValueProvider may already have outsmarted us and parsed
            // the wire representation (i.e. POCO). If the provider reads xml directly, would it know the
            // type? Would it convert it to a .NET native type? How to check?

            // The spec has no regexes for the primitives mentioned below, so don't check them
            bool hasSingleRegExForValue = definition.Type.Count() == 1 && definition.Type.First().GetPrimitiveValueRegEx() != null;

            if (hasSingleRegExForValue)
            {
                var primitiveRegEx = definition.Type.First().GetPrimitiveValueRegEx();
                var value = toStringRepresentation(instance);
                var success = Regex.Match(value, "^" + primitiveRegEx + "$").Success;

                if (!success)
                    Trace(outcome, $"Primitive value '{value}' does not match regex '{primitiveRegEx}'", Issue.CONTENT_ELEMENT_INVALID_PRIMITIVE_VALUE, instance);
            }

            return outcome;
        }


        internal OperationOutcome ValidateMaxLength(ElementDefinition definition, IElementNavigator instance)
        {
            var outcome = new OperationOutcome();

            if (definition.MaxLength != null)
            {
                var maxLength = definition.MaxLength.Value;

                if (maxLength > 0)
                {
                    if (instance.Value != null)
                    {
                        //TODO: Is ToString() really the right way to turn (Fhir?) Primitives back into their original representation?
                        //If the source is POCO, hopefully FHIR types have all overloaded ToString() 
                        var serializedValue = instance.Value.ToString();

                        if (serializedValue.Length > maxLength)
                            Trace(outcome, $"Value '{serializedValue}' is too long (maximum length is {maxLength})", Issue.CONTENT_ELEMENT_VALUE_TOO_LONG, instance);
                    }
                }
                else
                    Trace(outcome, $"MaxLength was given in ElementDefinition, but it has a negative value ({maxLength})", Issue.PROFILE_ELEMENTDEF_MAXLENGTH_NEGATIVE, instance);
            }

            return outcome;
        }


        internal void Trace(OperationOutcome outcome, string message, Issue issue, IElementNavigator location)
        {
            if (Settings.Trace || issue.Severity != OperationOutcome.IssueSeverity.Information)
                outcome.AddIssue(message, issue, location);
        }

        private string toStringRepresentation(IValueProvider vp)
        {
            if (vp == null || vp.Value == null) return null;

            var val = vp.Value;

            if (val is string)
                return (string)val;
            else if (val is long)
                return XmlConvert.ToString((long)val);
            else if (val is decimal)
                return XmlConvert.ToString((decimal)val);
            else if (val is bool)
                return (bool)val ? "true" : "false";
            else if (val is Hl7.FluentPath.Time)
                return ((Hl7.FluentPath.Time)val).ToString();
            else if (val is Hl7.FluentPath.PartialDateTime)
                return ((Hl7.FluentPath.PartialDateTime)val).ToString();
            else
                return val.ToString();
        }

        internal IElementNavigator ExternalReferenceResolutionNeeded(string reference, OperationOutcome outcome, IElementNavigator instance)
        {
            if (!Settings.ResolveExteralReferences) return null;

            try
            {
                // Default implementation: call event
                if (OnExternalResolutionNeeded != null)
                {
                    var args = new OnResolveResourceReferenceEventArgs(reference);
                    OnExternalResolutionNeeded(this, args);
                    return args.Result;
                }
            }
            catch(Exception e)
            {
                Trace(outcome, "External resolution of '{reference}' caused an error: " + e.Message, Issue.UNAVAILABLE_REFERENCED_RESOURCE, instance);
            }

            // Else, try to resolve using the given ResourceResolver 
            // (note: this also happens when the external resolution above threw an exception)
            if (Settings.ResourceResolver != null)
            {
                try
                {
                    var poco = Settings.ResourceResolver.ResolveByUri(reference);
                    if(poco != null)
                        return new PocoNavigator(poco);
                }
                catch(Exception e)
                {
                    Trace(outcome, $"Resolution of reference '{reference}' using the Resolver API failed: " + e.Message, Issue.UNAVAILABLE_REFERENCED_RESOURCE, instance);
                }
            }

            return null;        // Sorry, nothing worked
        }


        private StructureDefinition profileResolutionNeeded(string canonical)
        {
            if (Settings.ResourceResolver != null)
                return Settings.ResourceResolver.FindStructureDefinition(canonical);
            else
                return null;
        }


        // Note: this modifies an SD that is passed to us and will alter a possibly cached
        // object shared amongst other threads. This is generally useful and saves considerable
        // time when the same snapshot is needed again, but may result in side-effects
        private void snapshotGenerationNeeded(StructureDefinition definition)
        {
            if (!Settings.GenerateSnapshot) return;

            // Default implementation: call event
            if (OnSnapshotNeeded != null)
            {
                OnSnapshotNeeded(this, new OnSnapshotNeededEventArgs(definition, Settings.ResourceResolver));
                return;
            }

            // Else, expand, depending on our configuration
            if (Settings.ResourceResolver != null)
            {
                SnapshotGeneratorSettings settings = Settings.GenerateSnapshotSettings ?? SnapshotGeneratorSettings.Default;

                (new SnapshotGenerator(Settings.ResourceResolver, settings)).Update(definition);
            }
        }
    }



    internal static class TypeExtensions
    {
        // This is allowed for the types date, dateTime, instant, time, decimal, integer, and Quantity. string? why not?
        public static bool IsOrderedFhirType(this Type t)
        {
            return t == typeof(FhirDateTime) ||
                   t == typeof(Date) ||
                   t == typeof(Instant) ||
                   t == typeof(Model.Time) ||
                   t == typeof(FhirDecimal) ||
                   t == typeof(Integer) ||
                   t == typeof(Model.Quantity) ||
                   t == typeof(FhirString);
        }

        public static bool IsBindeableFhirType(this FHIRDefinedType t)
        {
            return t == FHIRDefinedType.Code ||
                   t == FHIRDefinedType.Coding ||
                   t == FHIRDefinedType.CodeableConcept ||
                   t == FHIRDefinedType.Quantity ||
                   t == FHIRDefinedType.Extension ||
                   t == FHIRDefinedType.String ||
                   t == FHIRDefinedType.Uri;
        }
    }


    public class OnSnapshotNeededEventArgs : EventArgs
    {
        public OnSnapshotNeededEventArgs(StructureDefinition definition, IResourceResolver resolver)
        {
            Definition = definition;
            Resolver = resolver;
        }

        public StructureDefinition Definition { get; }

        public IResourceResolver Resolver { get; }
    }

    public class OnResolveResourceReferenceEventArgs : EventArgs
    {
        public OnResolveResourceReferenceEventArgs(string reference)
        {
            Reference = reference;
        }

        public string Reference { get; }

        public IElementNavigator Result { get; set; }
    }


    public enum BatchValidationMode
    {
        All,
        Any,
        Once
    }
}
