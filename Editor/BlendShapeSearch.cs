using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace Nyako.SearchInlineBlendshapes
{
    // Replaces SkinnedMeshRendererEditor.OnBlendShapeUI with a copy that adds a search field and weight filter
    [InitializeOnLoad]
    internal static class BlendShapeSearch
    {
        private const string HarmonyId = "cat.nyako.search-inline-blendshapes";
        private const string SearchKey = "Nyako.SearchInlineBlendshapes.Search";
        private const string FilterKey = "Nyako.SearchInlineBlendshapes.Filter";

        private enum Filter { All, NonZero, Zero }

        private delegate void PropertySlider(SerializedProperty property, float sliderMin, float sliderMax, float textMin, float textMax, GUIContent label, GUILayoutOption[] options);
        private delegate float ValueSlider(GUIContent label, float value, float sliderMin, float sliderMax, float textMin, float textMax, GUILayoutOption[] options);

        private static readonly GUIContent Label = new GUIContent();
        private static readonly List<int> Matches = new List<int>();
        private static PropertySlider propertySlider;
        private static ValueSlider valueSlider;
        private static SearchField searchField;
        private static string search = SessionState.GetString(SearchKey, "");
        private static Filter filter = (Filter)SessionState.GetInt(FilterKey, 0);

        static BlendShapeSearch()
        {
            var harmony = new Harmony(HarmonyId);
            try
            {
                var editor = AccessTools.TypeByName("UnityEditor.SkinnedMeshRendererEditor");
                var target = AccessTools.DeclaredMethod(editor, "OnBlendShapeUI", Type.EmptyTypes)
                             ?? throw new MissingMethodException("SkinnedMeshRendererEditor", "OnBlendShapeUI");
                // Internal overloads let the text field go past the slider range, matching the stock inspector
                propertySlider = SliderDelegate<PropertySlider>(typeof(SerializedProperty), typeof(float), typeof(float), typeof(float), typeof(float), typeof(GUIContent), typeof(GUILayoutOption[]));
                valueSlider = SliderDelegate<ValueSlider>(typeof(GUIContent), typeof(float), typeof(float), typeof(float), typeof(float), typeof(float), typeof(GUILayoutOption[]));
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(BlendShapeSearch), nameof(Draw)));
                AssemblyReloadEvents.beforeAssemblyReload += () => harmony.UnpatchAll(HarmonyId);
            }
            catch (Exception ex)
            {
                harmony.UnpatchAll(HarmonyId);
                Debug.LogWarning("Search Inline Blendshapes unavailable: " + ex.Message);
            }
        }

        private static T SliderDelegate<T>(params Type[] parameters) where T : Delegate
        {
            var method = AccessTools.Method(typeof(EditorGUILayout), "Slider", parameters)
                         ?? throw new MissingMethodException("EditorGUILayout", "Slider");
            return (T)Delegate.CreateDelegate(typeof(T), method);
        }

        private static bool Draw(Editor __instance)
        {
            var mesh = ((SkinnedMeshRenderer)__instance.target).sharedMesh;
            var count = mesh == null ? 0 : mesh.blendShapeCount;
            if (count == 0) return false;

            var weights = __instance.serializedObject.FindProperty("m_BlendShapeWeights");
            Label.text = "BlendShapes";
            EditorGUILayout.PropertyField(weights, Label, false);
            if (!weights.isExpanded) return false;

            EditorGUI.indentLevel++;
            if (PlayerSettings.legacyClampBlendShapeWeights)
                EditorGUILayout.HelpBox("Note that BlendShape weight range is clamped. This can be disabled in Player Settings.", MessageType.Info);

            var arraySize = weights.arraySize;
            CollectMatches(mesh, weights, arraySize);
            DrawToolbar(count);

            if (Matches.Count == 0)
                EditorGUILayout.LabelField("No matching blendshapes", EditorStyles.centeredGreyMiniLabel);

            foreach (var i in Matches)
            {
                Label.text = mesh.GetBlendShapeName(i);
                float min = 0f, max = 0f;
                for (var f = 0; f < mesh.GetBlendShapeFrameCount(i); f++)
                {
                    var weight = mesh.GetBlendShapeFrameWeight(i, f);
                    min = Mathf.Min(weight, min);
                    max = Mathf.Max(weight, max);
                }

                if (i < arraySize)
                {
                    propertySlider(weights.GetArrayElementAtIndex(i), min, max, float.MinValue, float.MaxValue, Label, Array.Empty<GUILayoutOption>());
                    continue;
                }

                // Serialized weights can be shorter than the mesh's blendshape list; grow it on first edit like Unity does
                EditorGUI.BeginChangeCheck();
                var value = valueSlider(Label, 0f, min, max, float.MinValue, float.MaxValue, Array.Empty<GUILayoutOption>());
                if (EditorGUI.EndChangeCheck())
                {
                    weights.arraySize = count;
                    arraySize = count;
                    weights.GetArrayElementAtIndex(i).floatValue = value;
                }
            }

            EditorGUI.indentLevel--;
            return false;
        }

        private static void CollectMatches(Mesh mesh, SerializedProperty weights, int arraySize)
        {
            Matches.Clear();
            var terms = search.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < mesh.blendShapeCount; i++)
            {
                if (filter != Filter.All)
                {
                    var nonZero = i < arraySize && weights.GetArrayElementAtIndex(i).floatValue != 0f;
                    if (nonZero != (filter == Filter.NonZero)) continue;
                }

                var name = mesh.GetBlendShapeName(i);
                if (Array.TrueForAll(terms, t => name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))
                    Matches.Add(i);
            }
        }

        private static void DrawToolbar(int count)
        {
            var row = EditorGUI.IndentedRect(EditorGUILayout.GetControlRect());
            var indent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;

            const float filterWidth = 80f, countWidth = 64f, gap = 4f;
            var searchRect = new Rect(row.x, row.y + 1f, row.width - filterWidth - countWidth - gap * 2, row.height);
            var filterRect = new Rect(searchRect.xMax + gap, row.y, filterWidth, row.height);
            var countRect = new Rect(filterRect.xMax + gap, row.y, countWidth, row.height);

            searchField ??= new SearchField();
            EditorGUI.BeginChangeCheck();
            search = searchField.OnGUI(searchRect, search);
            filter = (Filter)EditorGUI.EnumPopup(filterRect, filter);
            if (EditorGUI.EndChangeCheck())
            {
                SessionState.SetString(SearchKey, search);
                SessionState.SetInt(FilterKey, (int)filter);
            }

            EditorGUI.LabelField(countRect, Matches.Count + " / " + count, EditorStyles.miniLabel);
            EditorGUI.indentLevel = indent;
        }
    }
}
