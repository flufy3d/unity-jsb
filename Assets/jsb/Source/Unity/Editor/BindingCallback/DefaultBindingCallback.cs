using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;

namespace QuickJS.Unity
{
    using UnityEngine;
    using UnityEditor;

    public class DefaultBindingCallback : IBindingCallback
    {
        public bool OnTypeGenerating(TypeBindingInfo typeBindingInfo, int current, int total)
        {
            return EditorUtility.DisplayCancelableProgressBar(
                "Generating",
                $"{current}/{total}: {typeBindingInfo.FullName}",
                (float)current / total);
        }

        public void OnGenerateFinish()
        {
            EditorUtility.ClearProgressBar();
        }
    }
}