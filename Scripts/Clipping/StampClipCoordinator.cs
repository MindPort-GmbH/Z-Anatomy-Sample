using System.Collections.Generic;
using EasyButtons;
using UnityEngine;

namespace VIRTOSHA.ZAnatomy.Clipping
{
    /// <summary>
    /// Single global writer for stamp clipping shader globals.
    /// Aggregates stamp matrices from multiple source owners and publishes one merged result.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("VIRTOSHA/Z-Anatomy/Stamp Clip Coordinator")]
    public class StampClipCoordinator : MonoBehaviour
    {
        private const int MaxShaderStamps = 64;
        private const string StampEnabledProperty = "_StampClipEnabled";
        private const string StampCountProperty = "_SphereStampCount";
        private const string StampWorldToLocalProperty = "_SphereStampWorldToLocal";

        [SerializeField, Tooltip("Enables debug logs for source registration and global stamp updates.")]
        private bool debugLogs;

        [SerializeField, Tooltip("Current merged stamp count that is written to shader globals.")]
        private int currentMergedCount;

        [SerializeField, Tooltip("Current number of registered stamp sources.")]
        private int registeredSourceCount;

        private readonly Dictionary<int, SourceState> sources = new Dictionary<int, SourceState>();
        private readonly List<SourceState> orderedSources = new List<SourceState>();
        private readonly List<Matrix4x4> mergedMatrices = new List<Matrix4x4>(MaxShaderStamps);
        private readonly Matrix4x4[] matrixBuffer = new Matrix4x4[MaxShaderStamps];

        private int stampEnabledID;
        private int stampCountID;
        private int stampWorldToLocalID;
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
            if (sourceOwner == null)
            {
                Debug.LogWarning($"[{nameof(StampClipCoordinator)}:{name}] Ignored SetSourceMatrices with null source owner.", this);
                return;
            }

            int sourceId = sourceOwner.GetInstanceID();
            if (!sources.TryGetValue(sourceId, out SourceState state))
            {
                state = new SourceState(sourceOwner);
                sources[sourceId] = state;
            }

            state.Matrices.Clear();
            if (matrices != null)
            {
                for (int i = 0; i < matrices.Count; i++)
                {
                    state.Matrices.Add(matrices[i]);
                }
            }

            if (state.Matrices.Count == 0)
            {
                sources.Remove(sourceId);
                LogDebug($"Removed source '{sourceOwner.name}' because it has no matrices.");
            }
            else
            {
                state.Sequence = ++updateSequence;
                sources[sourceId] = state;
                LogDebug($"Updated source '{sourceOwner.name}' with {state.Matrices.Count} matrix/matrices.");
            }

            isDirty = true;
        }

        public void ClearSource(Object sourceOwner)
        {
            if (sourceOwner == null)
            {
                return;
            }

            if (sources.Remove(sourceOwner.GetInstanceID()))
            {
                LogDebug($"Cleared source '{sourceOwner.name}'.");
                isDirty = true;
            }
        }

        public void ClearAllSources()
        {
            if (sources.Count == 0)
            {
                return;
            }

            sources.Clear();
            isDirty = true;
            LogDebug("Cleared all sources.");
        }

        private void Awake()
        {
            EnsurePropertyIDs();
            ClearGlobals();
        }

        private void OnEnable()
        {
            EnsurePropertyIDs();
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
            propertyIdsInitialized = true;
        }

        private void ApplyIfDirty()
        {
            if (!isDirty)
            {
                return;
            }

            EnsurePropertyIDs();
            RemoveDestroyedSources();
            BuildMergedMatrices();
            PublishGlobals();
            isDirty = false;
        }

        private void RemoveDestroyedSources()
        {
            List<int> removeIds = null;

            foreach (KeyValuePair<int, SourceState> pair in sources)
            {
                if (pair.Value.Owner != null)
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
                sources.Remove(removeIds[i]);
            }
        }

        private void BuildMergedMatrices()
        {
            orderedSources.Clear();
            mergedMatrices.Clear();

            foreach (KeyValuePair<int, SourceState> pair in sources)
            {
                orderedSources.Add(pair.Value);
            }

            orderedSources.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));

            for (int i = 0; i < orderedSources.Count; i++)
            {
                List<Matrix4x4> sourceMatrices = orderedSources[i].Matrices;
                for (int matrixIndex = 0; matrixIndex < sourceMatrices.Count; matrixIndex++)
                {
                    mergedMatrices.Add(sourceMatrices[matrixIndex]);
                    if (mergedMatrices.Count > MaxShaderStamps)
                    {
                        mergedMatrices.RemoveAt(0);
                    }
                }
            }

            registeredSourceCount = orderedSources.Count;
            currentMergedCount = mergedMatrices.Count;
        }

        private void PublishGlobals()
        {
            for (int i = 0; i < MaxShaderStamps; i++)
            {
                matrixBuffer[i] = Matrix4x4.identity;
            }

            for (int i = 0; i < mergedMatrices.Count; i++)
            {
                matrixBuffer[i] = mergedMatrices[i];
            }

            Shader.SetGlobalFloat(stampEnabledID, currentMergedCount > 0 ? 1.0f : 0.0f);
            Shader.SetGlobalFloat(stampCountID, currentMergedCount);
            Shader.SetGlobalMatrixArray(stampWorldToLocalID, matrixBuffer);

            LogDebug($"Published globals: sources={registeredSourceCount}, count={currentMergedCount}.");
        }

        private void ClearGlobals()
        {
            EnsurePropertyIDs();

            for (int i = 0; i < MaxShaderStamps; i++)
            {
                matrixBuffer[i] = Matrix4x4.identity;
            }

            Shader.SetGlobalFloat(stampEnabledID, 0.0f);
            Shader.SetGlobalFloat(stampCountID, 0.0f);
            Shader.SetGlobalMatrixArray(stampWorldToLocalID, matrixBuffer);
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
        /// Per-source cached matrix payload and recency sequence for merge ordering.
        /// </summary>
        private sealed class SourceState
        {
            public SourceState(Object owner)
            {
                Owner = owner;
            }

            public Object Owner { get; }
            public List<Matrix4x4> Matrices { get; } = new List<Matrix4x4>();
            public long Sequence { get; set; }
        }
    }
}
