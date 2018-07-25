/*  
* Copyright (c) 2018, Furore (info@furore.com) and contributors 
* See the file CONTRIBUTORS for details. 
*  
* This file is licensed under the BSD 3-Clause license 
* available at https://raw.githubusercontent.com/ewoutkramer/fhir-net-api/master/LICENSE 
*/

using Hl7.Fhir.ElementModel;
using Hl7.Fhir.Utility;
using Newtonsoft.Json.Linq;

namespace Hl7.Fhir.Serialization
{
    public class JsonSerializationDetails : IPositionInfo
    {
        public const string RESOURCETYPE_MEMBER_NAME = "resourceType";

        public object OriginalValue;

        public bool IsArrayElement;
        public int LineNumber { get; internal set; }
        public int LinePosition { get; internal set; }

    }

    public static class JsonSerializationDetailsExtensions
    {
        public static JsonSerializationDetails GetJsonSerializationDetails(this IAnnotated ann) =>
                ann.TryGetAnnotation<JsonSerializationDetails>(out var rt) ? rt : null;

        public static JsonSerializationDetails GetJsonSerializationDetails(this IElementNavigator navigator) =>
            navigator is IAnnotated ia ? ia.GetJsonSerializationDetails() : null;

        public static JsonSerializationDetails GetJsonSerializationDetails(this ISourceNavigator navigator) =>
            navigator is IAnnotated ia ? ia.GetJsonSerializationDetails() : null;
    }
}