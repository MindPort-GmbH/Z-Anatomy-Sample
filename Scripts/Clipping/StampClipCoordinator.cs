using System.Collections.Generic;
using EasyButtons;
using UnityEngine;

namespace VIRTOSHA.ZAnatomy.Clipping
{
    /// <summary>
    /// Single global writer for stamp clipping shader globals.
    /// Aggregates stamp matrices from multiple source owners and publishes one merged result.
    /// Also routes source influence to target renderers/materials via a per-target source mask.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("VIRTOSHA/Z-Anatomy/Stamp Clip Coordinator")]
    public class StampClipCoordinator : MonoBehaviour
    {
        private const int MaxShaderStamps = 64;
        private const int MaxSourceBits = 32;

        private const string StampEnabledProperty = "_StampClipEnabled";
        private const string StampCountProperty = "_SphereStampCount";
        private const string StampWorldToLocalProperty = "_SphereStampWorldToLocal";
        private const string StampSourceIndexProperty = "_SphereStampSourceIndex";
        private const string StampSourceMaskLowProperty = "_StampClipSourceMask";
        private const string StampSourceMaskHighProperty = "_StampClipSourceMaskHigh";

        [SerializeField, Tooltip("Enables debug logs for source registration and global stamp updates.")]
        private bool debugLogs;

        [SerializeField, Tooltip("Current merged stamp count that is written to shader globals.")]
        private int currentMergedCount;

        [SerializeField, Tooltip("Current number of registered stamp sources.")]
        private int registeredSourceCount;

        private readonly Dictionary<int, SourceState> sources = new Dictionary<int, SourceState>();
        private readonly List<SourceState> orderedSources = new List<SourceState>();
        private readonly List<Matrix4x4> mergedMatrices = new List<Matrix4x4>(MaxShaderStamps);
        private readonly List<int> mergedSourceIndices = new List<int>(MaxShaderStamps);

        private readonly Matrix4x4[] matrixBuffer = new Matrix4x4[MaxShaderStamps];
        private readonly float[] sourceIndexBuffer = new float[MaxShaderStamps];
        private readonly bool[] usedSourceBits = new bool[MaxSourceBits];

        private readonly Dictionary<Renderer, uint> rendererMasks = new Dictionary<Renderer, uint>();
        private readonly Dictionary<Material, uint> materialMasks = new Dictionary<Material, uint>();
        private readonly HashSet<Renderer> lastAppliedRenderers = new HashSet<Renderer>();
        private readonly HashSet<Material> lastAppliedMaterials = new HashSet<Material>();
        private readonly HashSet<int> warnedMaterialsMissingMaskProperty = new HashSet<int>();
        private MaterialPropertyBlock propertyBlock;

        private int stampEnabledID;
        private int stampCountID;
        private int stampWorldToLocalID;
        private int stampSourceIndexID;
        private int stampSourceMaskLowID;
        private int stampSourceMaskHighID;

        private long updateSequence;
        private bool propertyIdsInitialized;
        private bool isDirty = true;

        public int CurrentMergedCount => currentMergedCount;
        public int RegisteredSourceCount => registeredSourceCount;

        [Button]
        public void RefreshNow()
        {
            isDirty = true;
            ApplyIfDirty();
        }

        public void SetSourceMatrices(Object sourceOwner, IReadOnlyList<Matrix4x4> matrices)
        {
            SetSourceStateInternal(sourceOwner, matrices, null, null, updateMatrices: true, updateTargets: false);
        }

        public void SetSourceTargets(Object sourceOwner, IReadOnlyList<Renderer> targetRenderers, IReadOnlyList<Material> targetMaterials)
        {
            SetSourceStateInternal(sourceOwner, null, targetRenderers, targetMaterials, updateMatrices: false, updateTargets: true);
        }

        public void SetSourceState(
            Object sourceOwner,
            IReadOnlyList<Matrix4x4> matrices,
            IReadOnlyList<Renderer> targetRenderers,
            IReadOnlyList<Material> targetMaterials)
        {
            SetSourceStateInternal(sourceOwner, matrices, targetRenderers, targetMaterials, updateMatrices: true, updateTargets: true);
        }

        public void ClearSource(Object sourceOwner)
        {
            if (sourceOwner == null)
            {
                return;
            }

            int sourceId = sourceOwner.GetInstanceID();
            if (!sources.TryGetValue(sourceId, out SourceState state))
            {
                return;
            }

            ReleaseSourceBit(state);
            sources.Remove(sourceId);
            LogDebug($"Cleared source '{sourceOwner.name}'.");
            isDirty = true;
        }

        public void ClearAllSources()
        {
            if (sources.Count == 0)
            {
                return;
            }

            foreach (KeyValuePair<int, SourceState> pair in sources)
            {
                ReleaseSourceBit(pair.Value);
            }

            sources.Clear();
            isDirty = true;
            LogDebug("Cleared all sources.");
        }

        private void Awake()
        {
            EnsurePropertyIDs();
            EnsurePropertyBlock();
            ClearGlobals();
            ClearAllTargetMasks();
        }

        private void OnEnable()
        {
            EnsurePropertyIDs();
            EnsurePropertyBlock();
            isDirty = true;
            ApplyIfDirty();
        }

        private void LateUpdate()
        {
            ApplyIfDirty();
        }

        private void OnDisable()
        {
            currentMergedCount = 0;
            registeredSourceCount = 0;
            ClearGlobals();
            ClearAllTargetMasks();
        }

        private void EnsurePropertyIDs()
        {
            if (propertyIdsInitialized)
            {
                return;
            }

            stampEnabledID = Shader.PropertyToID(StampEnabledProperty);
            stampCountID = Shader.PropertyToID(StampCountProperty);
            stampWorldToLocalID = Shader.PropertyToID(StampWorldToLocalProperty);
            stampSourceIndexID = Shader.PropertyToID(StampSourceIndexProperty);
            stampSourceMaskLowID = Shader.PropertyToID(StampSourceMaskLowProperty);
            stampSourceMaskHighID = Shader.PropertyToID(StampSourceMaskHighProperty);
            propertyIdsInitialized = true;
        }

        private void SetSourceStateInternal(
            Object sourceOwner,
            IReadOnlyList<Matrix4x4> matrices,
            IReadOnlyList<Renderer> targetRenderers,
            IReadOnlyList<Material> targetMaterials,
            bool updateMatrices,
            bool updateTargets)
        {
            if (sourceOwner == null)
            {
                Debug.LogWarning($"[{nameof(StampClipCoordinator)}:{name}] Ignored source update with null source owner.", this);
                return;
            }

            int sourceId = sourceOwner.GetInstanceID();
            if (!sources.TryGetValue(sourceId, out SourceState state))
            {
                state = new SourceState(sourceOwner);
                sources[sourceId] = state;
            }

            if (updateMatrices)
            {
                state.Matrices.Clear();
                if (matrices != null)
                {
                    for (int i = 0; i < matrices.Count; i++)
                    {
                        state.Matrices.Add(matrices[i]);
                    }
                }
            }

            if (updateTargets)
            {
                CopyTargets(state.TargetRenderers, targetRenderers);
                CopyValidTargetMaterials(sourceOwner, state.TargetMaterials, targetMaterials);
            }

            if (state.Matrices.Count == 0 && state.TargetRenderers.Count == 0 && state.TargetMaterials.Count == 0)
            {
                ReleaseSourceBit(state);
                sources.Remove(sourceId);
                LogDebug($"Removed source '{sourceOwner.name}' because it has no matrices and no targets.");
                isDirty = true;
                return;
            }

            bool needsSourceBit = state.TargetRenderers.Count > 0 || state.TargetMaterials.Count > 0;
            if (needsSourceBit)
            {
                TryAssignSourceBit(state);
            }
            else
            {
                ReleaseSourceBit(state);
            }

            state.Sequence = ++updateSequence;
            sources[sourceId] = state;

            LogDebug(
                $"Updated source '{sourceOwner.name}': matrices={state.Matrices.Count}, " +
                $"renderers={state.TargetRenderers.Count}, materials={state.TargetMaterials.Count}, bit={state.SourceBitIndex}.");

            isDirty = true;
        }

        private void ApplyIfDirty()
        {
            if (!isDirty)
            {
                return;
            }

            EnsurePropertyIDs();
            RemoveDestroyedSources();
            RetryAssignUnassignedBits();
            BuildMergedMatrices();
            bool removedEmptySources = BuildTargetMasks();
            PublishGlobals();
            PublishTargetMasks();
            isDirty = removedEmptySources;
        }

        private void RemoveDestroyedSources()
        {
            List<int> removeIds = null;

            foreach (KeyValuePair<int, SourceState> pair in sources)
            {
                SourceState state = pair.Value;
                if (state.Owner != null)
                {
                    continue;
                }

                removeIds ??= new List<int>();
                removeIds.Add(pair.Key);
            }

            if (removeIds == null)
            {
                return;
            }

            for (int i = 0; i < removeIds.Count; i++)
            {
                int sourceId = removeIds[i];
                if (!sources.TryGetValue(sourceId, out SourceState state))
                {
                    continue;
                }

                ReleaseSourceBit(state);
                sources.Remove(sourceId);
            }
        }

        private void BuildMergedMatrices()
        {
            orderedSources.Clear();
            mergedMatrices.Clear();
            mergedSourceIndices.Clear();

            foreach (KeyValuePair<int, SourceState> pair in sources)
            {
                orderedSources.Add(pair.Value);
            }

            orderedSources.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));

            for (int sourceIndex = 0; sourceIndex < orderedSources.Count; sourceIndex++)
            {
                SourceState source = orderedSources[sourceIndex];
                bool hasTargets = source.TargetRenderers.Count > 0 || source.TargetMaterials.Count > 0;
                if (!hasTargets)
                {
                    // Unscoped sources are intentionally ignored to keep clipping target-scoped only.
                    continue;
                }

                if (source.SourceBitIndex < 0)
                {
                    // Routing is required for this source but no bit could be assigned.
                    continue;
                }

                int publishedSourceIndex = source.SourceBitIndex;
                List<Matrix4x4> sourceMatrices = source.Matrices;

                for (int matrixIndex = 0; matrixIndex < sourceMatrices.Count; matrixIndex++)
                {
                    mergedMatrices.Add(sourceMatrices[matrixIndex]);
                    mergedSourceIndices.Add(publishedSourceIndex);

                    if (mergedMatrices.Count > MaxShaderStamps)
                    {
                        mergedMatrices.RemoveAt(0);
                        mergedSourceIndices.RemoveAt(0);
                    }
                }
            }

            registeredSourceCount = orderedSources.Count;
            currentMergedCount = mergedMatrices.Count;
        }

        private void RetryAssignUnassignedBits()
        {
            bool hasFreeBit = false;
            for (int bitIndex = 0; bitIndex < MaxSourceBits; bitIndex++)
            {
                if (usedSourceBits[bitIndex])
                {
                    continue;
                }

                hasFreeBit = true;
                break;
            }

            if (!hasFreeBit)
            {
                return;
            }

            orderedSources.Clear();
            foreach (KeyValuePair<int, SourceState> pair in sources)
            {
                orderedSources.Add(pair.Value);
            }

            orderedSources.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));

            for (int sourceIndex = 0; sourceIndex < orderedSources.Count; sourceIndex++)
            {
                SourceState source = orderedSources[sourceIndex];
                bool hasTargets = source.TargetRenderers.Count > 0 || source.TargetMaterials.Count > 0;
                if (!hasTargets || source.SourceBitIndex >= 0)
                {
                    continue;
                }

                if (!TryAssignSourceBit(source))
                {
                    break;
                }
            }
        }

        private bool BuildTargetMasks()
        {
            rendererMasks.Clear();
            materialMasks.Clear();
            List<int> removeSourceIds = null;

            foreach (KeyValuePair<int, SourceState> pair in sources)
            {
                int sourceId = pair.Key;
                SourceState state = pair.Value;
                if (state.SourceBitIndex < 0 || state.SourceBitIndex >= MaxSourceBits || state.Matrices.Count == 0)
                {
                    continue;
                }

                uint bitMask = 1u << state.SourceBitIndex;

                for (int i = state.TargetRenderers.Count - 1; i >= 0; i--)
                {
                    Renderer renderer = state.TargetRenderers[i];
                    if (renderer == null)
                    {
                        state.TargetRenderers.RemoveAt(i);
                        continue;
                    }

                    if (rendererMasks.TryGetValue(renderer, out uint currentMask))
                    {
                        rendererMasks[renderer] = currentMask | bitMask;
                    }
                    else
                    {
                        rendererMasks[renderer] = bitMask;
                    }
                }

                for (int i = state.TargetMaterials.Count - 1; i >= 0; i--)
                {
                    Material material = state.TargetMaterials[i];
                    if (material == null)
                    {
                        state.TargetMaterials.RemoveAt(i);
                        continue;
                    }

                    if (materialMasks.TryGetValue(material, out uint currentMask))
                    {
                        materialMasks[material] = currentMask | bitMask;
                    }
                    else
                    {
                        materialMasks[material] = bitMask;
                    }
                }

                if (state.TargetRenderers.Count == 0 && state.TargetMaterials.Count == 0)
                {
                    ReleaseSourceBit(state);
                    removeSourceIds ??= new List<int>();
                    removeSourceIds.Add(sourceId);
                }
            }

            if (removeSourceIds == null)
            {
                return false;
            }

            for (int i = 0; i < removeSourceIds.Count; i++)
            {
                sources.Remove(removeSourceIds[i]);
            }

            return true;
        }

        private void PublishGlobals()
        {
            for (int i = 0; i < MaxShaderStamps; i++)
            {
                matrixBuffer[i] = Matrix4x4.identity;
                sourceIndexBuffer[i] = -1.0f;
            }

            for (int i = 0; i < mergedMatrices.Count; i++)
            {
                matrixBuffer[i] = mergedMatrices[i];
                sourceIndexBuffer[i] = mergedSourceIndices[i];
            }

            // Default for all untargeted materials/renderers.
            Shader.SetGlobalFloat(stampSourceMaskLowID, 0.0f);
            Shader.SetGlobalFloat(stampSourceMaskHighID, 0.0f);
            Shader.SetGlobalFloat(stampEnabledID, currentMergedCount > 0 ? 1.0f : 0.0f);
            Shader.SetGlobalFloat(stampCountID, currentMergedCount);
            Shader.SetGlobalMatrixArray(stampWorldToLocalID, matrixBuffer);
            Shader.SetGlobalFloatArray(stampSourceIndexID, sourceIndexBuffer);

            LogDebug($"Published globals: sources={registeredSourceCount}, count={currentMergedCount}.");
        }

        private void PublishTargetMasks()
        {
            foreach (Renderer renderer in lastAppliedRenderers)
            {
                if (renderer == null || rendererMasks.ContainsKey(renderer))
                {
                    continue;
                }

                ClearRendererMask(renderer);
            }

            foreach (Material material in lastAppliedMaterials)
            {
                if (material == null || materialMasks.ContainsKey(material))
                {
                    continue;
                }

                TrySetMaterialMask(material, 0u);
            }

            foreach (KeyValuePair<Renderer, uint> pair in rendererMasks)
            {
                if (pair.Key == null)
                {
                    continue;
                }

                uint effectiveRendererMask = pair.Value | GetMaterialMaskForRenderer(pair.Key);
                SetRendererMask(pair.Key, effectiveRendererMask);
            }

            foreach (KeyValuePair<Material, uint> pair in materialMasks)
            {
                if (pair.Key == null)
                {
                    continue;
                }

                TrySetMaterialMask(pair.Key, pair.Value);
            }

            lastAppliedRenderers.Clear();
            foreach (Renderer renderer in rendererMasks.Keys)
            {
                if (renderer != null)
                {
                    lastAppliedRenderers.Add(renderer);
                }
            }

            lastAppliedMaterials.Clear();
            foreach (Material material in materialMasks.Keys)
            {
                if (material != null)
                {
                    lastAppliedMaterials.Add(material);
                }
            }
        }

        private void SetRendererMask(Renderer renderer, uint mask)
        {
            if (mask == 0u)
            {
                ClearRendererMask(renderer);
                return;
            }

            PackSourceMask(mask, out float lowLane, out float highLane);

            EnsurePropertyBlock();
            propertyBlock.Clear();
            renderer.GetPropertyBlock(propertyBlock);
            propertyBlock.SetFloat(stampSourceMaskLowID, lowLane);
            propertyBlock.SetFloat(stampSourceMaskHighID, highLane);
            renderer.SetPropertyBlock(propertyBlock);
        }

        private void ClearRendererMask(Renderer renderer)
        {
            renderer.SetPropertyBlock(null);
        }

        private uint GetMaterialMaskForRenderer(Renderer renderer)
        {
            Material[] sharedMaterials = renderer.sharedMaterials;
            uint materialMask = 0u;

            for (int i = 0; i < sharedMaterials.Length; i++)
            {
                Material material = sharedMaterials[i];
                if (material != null && materialMasks.TryGetValue(material, out uint mask))
                {
                    materialMask |= mask;
                }
            }

            return materialMask;
        }

        private void EnsurePropertyBlock()
        {
            if (propertyBlock == null)
            {
                propertyBlock = new MaterialPropertyBlock();
            }
        }

        private void ClearGlobals()
        {
            EnsurePropertyIDs();

            for (int i = 0; i < MaxShaderStamps; i++)
            {
                matrixBuffer[i] = Matrix4x4.identity;
                sourceIndexBuffer[i] = -1.0f;
            }

            Shader.SetGlobalFloat(stampSourceMaskLowID, 0.0f);
            Shader.SetGlobalFloat(stampSourceMaskHighID, 0.0f);
            Shader.SetGlobalFloat(stampEnabledID, 0.0f);
            Shader.SetGlobalFloat(stampCountID, 0.0f);
            Shader.SetGlobalMatrixArray(stampWorldToLocalID, matrixBuffer);
            Shader.SetGlobalFloatArray(stampSourceIndexID, sourceIndexBuffer);
        }

        private void ClearAllTargetMasks()
        {
            foreach (Renderer renderer in lastAppliedRenderers)
            {
                if (renderer == null)
                {
                    continue;
                }

                ClearRendererMask(renderer);
            }

            foreach (Material material in lastAppliedMaterials)
            {
                if (material == null)
                {
                    continue;
                }

                TrySetMaterialMask(material, 0u);
            }

            lastAppliedRenderers.Clear();
            lastAppliedMaterials.Clear();
            rendererMasks.Clear();
            materialMasks.Clear();
        }

        private bool TryAssignSourceBit(SourceState state)
        {
            if (state.SourceBitIndex >= 0 && state.SourceBitIndex < MaxSourceBits)
            {
                return true;
            }

            for (int bit = 0; bit < MaxSourceBits; bit++)
            {
                if (usedSourceBits[bit])
                {
                    continue;
                }

                usedSourceBits[bit] = true;
                state.SourceBitIndex = bit;
                state.OverflowWarningLogged = false;
                return true;
            }

            state.SourceBitIndex = -1;
            if (!state.OverflowWarningLogged)
            {
                Debug.LogWarning(
                    $"[{nameof(StampClipCoordinator)}:{name}] Maximum routed sources ({MaxSourceBits}) reached. " +
                    $"Source '{state.Owner?.name}' will not receive a source bit.",
                    this);
                state.OverflowWarningLogged = true;
            }

            return false;
        }

        private void ReleaseSourceBit(SourceState state)
        {
            if (state.SourceBitIndex < 0 || state.SourceBitIndex >= MaxSourceBits)
            {
                state.SourceBitIndex = -1;
                return;
            }

            usedSourceBits[state.SourceBitIndex] = false;
            state.SourceBitIndex = -1;
            state.OverflowWarningLogged = false;
        }

        private static void CopyTargets<T>(List<T> destination, IReadOnlyList<T> source) where T : Object
        {
            destination.Clear();
            if (source == null)
            {
                return;
            }

            HashSet<int> seen = new HashSet<int>();
            for (int i = 0; i < source.Count; i++)
            {
                T item = source[i];
                if (item == null)
                {
                    continue;
                }

                int instanceId = item.GetInstanceID();
                if (seen.Add(instanceId))
                {
                    destination.Add(item);
                }
            }
        }

        private void CopyValidTargetMaterials(
            Object sourceOwner,
            List<Material> destination,
            IReadOnlyList<Material> source)
        {
            destination.Clear();
            if (source == null)
            {
                return;
            }

            EnsurePropertyIDs();

            HashSet<int> seen = new HashSet<int>();
            for (int i = 0; i < source.Count; i++)
            {
                Material material = source[i];
                if (material == null)
                {
                    continue;
                }

                int materialId = material.GetInstanceID();
                if (!SupportsStampMaskProperty(material))
                {
                    if (warnedMaterialsMissingMaskProperty.Add(materialId))
                    {
                        string ownerName = sourceOwner != null ? sourceOwner.name : "null";
                        Debug.LogError(
                            $"[{nameof(StampClipCoordinator)}:{name}] Source '{ownerName}' targeted material '{material.name}' " +
                            $"without required mask properties '{StampSourceMaskLowProperty}' and '{StampSourceMaskHighProperty}'. " +
                            "Material target is ignored.",
                            this);
                    }

                    continue;
                }

                if (seen.Add(materialId))
                {
                    destination.Add(material);
                }
            }
        }

        private void TrySetMaterialMask(Material material, uint mask)
        {
            if (!SupportsStampMaskProperty(material))
            {
                int materialId = material.GetInstanceID();
                if (warnedMaterialsMissingMaskProperty.Add(materialId))
                {
                    Debug.LogError(
                        $"[{nameof(StampClipCoordinator)}:{name}] Material '{material.name}' does not expose required " +
                        $"mask properties '{StampSourceMaskLowProperty}' and '{StampSourceMaskHighProperty}'. " +
                        "Mask write is skipped.",
                        this);
                }

                return;
            }

            PackSourceMask(mask, out float lowLane, out float highLane);
            material.SetFloat(stampSourceMaskLowID, lowLane);
            material.SetFloat(stampSourceMaskHighID, highLane);
        }

        private bool SupportsStampMaskProperty(Material material)
        {
            return material != null &&
                material.HasProperty(stampSourceMaskLowID) &&
                material.HasProperty(stampSourceMaskHighID);
        }

        private static void PackSourceMask(uint mask, out float lowLane, out float highLane)
        {
            lowLane = mask & 0xFFFFu;
            highLane = (mask >> 16) & 0xFFFFu;
        }

        private void LogDebug(string message)
        {
            if (!debugLogs)
            {
                return;
            }

            Debug.Log($"[{nameof(StampClipCoordinator)}:{name}] {message}", this);
        }

        /// <summary>
        /// Per-source cached payload and recency sequence for merge ordering.
        /// </summary>
        private sealed class SourceState
        {
            public SourceState(Object owner)
            {
                Owner = owner;
            }

            public Object Owner { get; }
            public List<Matrix4x4> Matrices { get; } = new List<Matrix4x4>();
            public List<Renderer> TargetRenderers { get; } = new List<Renderer>();
            public List<Material> TargetMaterials { get; } = new List<Material>();

            public int SourceBitIndex { get; set; } = -1;
            public long Sequence { get; set; }
            public bool OverflowWarningLogged { get; set; }
        }
    }
}
