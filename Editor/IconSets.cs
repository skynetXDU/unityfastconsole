using System;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Tags;
using UnityEngine;
using UnityEngine.UIElements;

namespace SKYNET {
    [CreateAssetMenu(fileName = "代码补全图标", menuName = "Unity Fast Console/补全图标集")]
    public class IconSets : ScriptableObject {

        [SerializeField]
        private VectorImage defaultIcon;

        [SerializeField]
        private VectorImage methodIcon;

        [SerializeField]
        private VectorImage propertyIcon;

        [SerializeField]
        private VectorImage fieldIcon;

        [SerializeField]
        private VectorImage parameterIcon;

        [SerializeField]
        private VectorImage variableIcon;

        [SerializeField]
        private VectorImage classIcon;

        [SerializeField]
        private VectorImage structureIcon;

        [SerializeField]
        private VectorImage interfaceIcon;

        [SerializeField]
        private VectorImage enumIcon;

        [SerializeField]
        private VectorImage enumMemberIcon;

        [SerializeField]
        private VectorImage keywordIcon;

        public VectorImage GetIconByCompletionItem(CompletionItem item) {
            if (item.Tags.Contains(WellKnownTags.Method))
                return methodIcon;

            if (item.Tags.Contains(WellKnownTags.Property))
                return propertyIcon;

            if (item.Tags.Contains(WellKnownTags.Field))
                return fieldIcon;

            if (item.Tags.Contains(WellKnownTags.Parameter))
                return parameterIcon;

            if (item.Tags.Contains(WellKnownTags.Local))
                return variableIcon;

            if (item.Tags.Contains(WellKnownTags.Class))
                return classIcon;

            if (item.Tags.Contains(WellKnownTags.Structure))
                return structureIcon;

            if (item.Tags.Contains(WellKnownTags.Interface))
                return interfaceIcon;

            if (item.Tags.Contains(WellKnownTags.Enum))
                return enumIcon;

            if (item.Tags.Contains(WellKnownTags.EnumMember))
                return enumMemberIcon;

            if (item.Tags.Contains(WellKnownTags.Keyword))
                return keywordIcon;

            return defaultIcon;
        }
    }
}