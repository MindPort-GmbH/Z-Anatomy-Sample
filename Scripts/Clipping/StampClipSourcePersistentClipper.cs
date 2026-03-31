using System;
using System.Collections.Generic;
using EasyButtons;
using Microsoft.MixedReality.GraphicsTools;
using UnityEngine;

namespace VIRTOSHA.ZAnatomy.Clipping
{
    /// <summary>
    /// Records clip stamps and forwards them to StampClipCoordinator.
    /// Targets are taken directly from configured renderer/material lists.
    /// Stamps remain active until <see cref="ResetClipping"/> is called.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("VIRTOSHA/Z-Anatomy/Stamp Clip Source Persistent Clipper")]
    public class StampClipSourcePersistentClipper : StampClipSourceBase
    {
        private const int MaxShaderStamps = 64;

        [Header("Stamping")]
        [SerializeField, Range(1, MaxShaderStamps), Tooltip("Maximum retained stamps. Hard-limited by shader setting.")]
        private int maxStamps = MaxShaderStamps;

        [SerializeField, Min(0.0f), Tooltip("Minimum position delta required before adding another stamp from motion.")]
        private float minStampTranslation = 0.0025f;

        [SerializeField, Range(0.0f, 180.0f), Tooltip("Minimum rotation delta in degrees required before adding another stamp from motion.")]
        private float minStampRotation = 1.0f;

        [Header("Debug")]
        [SerializeField, Tooltip("Enables StampClipSourcePersistentClipper diagnostic logs in the Unity Console.")]
        private bool debugLogs = true;

        [SerializeField, Tooltip("Renderers currently tracked as stamp targets.")]
        private List<Renderer> observedRenderers = new List<Renderer>();

        [SerializeField, Tooltip("Materials currently tracked for receiving stamp state updates.")]
        private List<Material> observedMaterials = new List<Material>();

        [SerializeField, Tooltip("Current number of stored stamps published to the coordinator.")]
        private int currentStampCount;

        private readonly HashSet<Renderer> observedRendererSet = new HashSet<Renderer>();
        private readonly HashSet<Material> observedMaterialSet = new HashSet<Material>();
        private readonly HashSet<Renderer> instanceMaterialOwners = new HashSet<Renderer>();
        private readonly List<Matrix4x4> sphereStampWorldToLocalMatrices = new List<Matrix4x4>();

        private bool hasLastStampPose;
        private Vector3 lastStampPosition;
        private Quaternion lastStampRotation;
        private bool hasPendingCoordinatorPublish;

        public IReadOnlyList<Material> AffectedMaterials => observedMaterials;

        private void Awake()
        {
            EnsureCoordinatorReference();
        }

        private void OnEnable()
        {
            EnsureCoordinatorReference();
            RebuildSetsFromLists();
            RefreshObservedTargetsFromConfiguredLists();
            PushStampStateToCoordinator();
        }

        private void OnDisable()
        {
            ClearCoordinatorSource();
        }

        private void OnDestroy()
        {
            ClearCoordinatorSource();
            ReleaseAllRendererMaterialOwnership();
            observedRendererSet.Clear();
            observedMaterialSet.Clear();
            observedRenderers.Clear();
            observedMaterials.Clear();
        }

        private void OnValidate()
        {
            maxStamps = Mathf.Clamp(maxStamps, 1, MaxShaderStamps);
            minStampTranslation = Mathf.Max(0.0f, minStampTranslation);
            minStampRotation = Mathf.Clamp(minStampRotation, 0.0f, 180.0f);
            EnsureCoordinatorReference();
        }

        protected override bool ShouldSync()
        {
            return continuousSync || hasPendingCoordinatorPublish;
        }

        protected override void PushUpdateToCoordinator()
        {
            if (continuousSync)
            {
                if (observedRenderers.Count == 0 && observedMaterials.Count == 0)
                {
                    RefreshObservedTargetsFromConfiguredLists();
                }

                if ((observedRenderers.Count > 0 || observedMaterials.Count > 0) && ShouldCaptureStampPose())
                {
                    CaptureStampFromCurrentPose();
                }
            }

            if (!hasPendingCoordinatorPublish)
            {
                return;
            }

            PushStampStateToCoordinator();
        }

        [Button]
        public void ResetClipping()
        {
            sphereStampWorldToLocalMatrices.Clear();
            currentStampCount = 0;
            hasLastStampPose = false;
            PushStampStateToCoordinator();
            LogDebug("ResetClipping: cleared all stored sphere stamps.");
        }

        [Button]
        public void ClearAffectedTargets()
        {
            ResetClipping();
            ReleaseAllRendererMaterialOwnership();
            observedRendererSet.Clear();
            observedMaterialSet.Clear();
            observedRenderers.Clear();
            observedMaterials.Clear();
            LogDebug("ClearAffectedTargets: cleared tracked renderers and materials.");
        }

        private void RefreshObservedTargetsFromConfiguredLists()
        {
            bool targetsChanged = false;

            ReleaseAllRendererMaterialOwnership();
            observedRendererSet.Clear();
            observedMaterialSet.Clear();
            observedRenderers.Clear();
            observedMaterials.Clear();

            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                if (observedRendererSet.Add(renderer))
                {
                    observedRenderers.Add(renderer);
                    targetsChanged = true;
                }

                Material[] rendererMaterials = AcquireRendererMaterials(renderer, instance: true);
                for (int materialIndex = 0; materialIndex < rendererMaterials.Length; materialIndex++)
                {
                    Material material = rendererMaterials[materialIndex];
                    if (material == null)
                    {
                        continue;
                    }

                    if (observedMaterialSet.Add(material))
                    {
                        observedMaterials.Add(material);
                        targetsChanged = true;
                    }
                }
            }

            for (int i = 0; i < materials.Count; i++)
            {
                Material material = materials[i];
                if (material == null)
                {
                    continue;
                }

                if (observedMaterialSet.Add(material))
                {
                    observedMaterials.Add(material);
                    targetsChanged = true;
                }
            }

            if (targetsChanged && sphereStampWorldToLocalMatrices.Count > 0)
            {
                hasPendingCoordinatorPublish = true;
            }
        }

        private Material[] AcquireRendererMaterials(Renderer renderer, bool instance = true)
        {
            if (renderer == null)
            {
                return Array.Empty<Material>();
            }

            if (applyToSharedMaterial)
            {
                return renderer.sharedMaterials;
            }

            MaterialInstance materialInstance = renderer.EnsureComponent<MaterialInstance>();
            Material[] acquiredMaterials = materialInstance.AcquireMaterials(this, instance);
            if (instance)
            {
                instanceMaterialOwners.Add(renderer);
            }

            return acquiredMaterials ?? Array.Empty<Material>();
        }

        private void ReleaseRendererMaterialOwnership(Renderer renderer, bool autoDestroy = true)
        {
            if (applyToSharedMaterial || renderer == null || !instanceMaterialOwners.Contains(renderer))
            {
                return;
            }

            MaterialInstance materialInstance = renderer.GetComponent<MaterialInstance>();
            if (materialInstance != null)
            {
                materialInstance.ReleaseMaterial(this, autoDestroy);
            }

            instanceMaterialOwners.Remove(renderer);
        }

        private void ReleaseAllRendererMaterialOwnership(bool autoDestroy = true)
        {
            if (instanceMaterialOwners.Count == 0)
            {
                return;
            }

            foreach (Renderer renderer in instanceMaterialOwners)
            {
                if (renderer == null)
                {
                    continue;
                }

                MaterialInstance materialInstance = renderer.GetComponent<MaterialInstance>();
                if (materialInstance != null)
                {
                    materialInstance.ReleaseMaterial(this, autoDestroy);
                }
            }

            instanceMaterialOwners.Clear();
        }

        private bool ShouldCaptureStampPose()
        {
            if (!hasLastStampPose)
            {
                return true;
            }

            float translationSq = (transform.position - lastStampPosition).sqrMagnitude;
            if (translationSq >= (minStampTranslation * minStampTranslation))
            {
                return true;
            }

            return Quaternion.Angle(transform.rotation, lastStampRotation) >= minStampRotation;
        }

        private void CaptureStampFromCurrentPose()
        {
            if (sphereStampWorldToLocalMatrices.Count >= maxStamps)
            {
                sphereStampWorldToLocalMatrices.RemoveAt(0);
                LogDebug($"CaptureStamp: reached maxStamps={maxStamps}, removed oldest stamp.");
            }

            sphereStampWorldToLocalMatrices.Add(transform.worldToLocalMatrix);
            currentStampCount = sphereStampWorldToLocalMatrices.Count;
            hasLastStampPose = true;
            lastStampPosition = transform.position;
            lastStampRotation = transform.rotation;
            hasPendingCoordinatorPublish = true;

            LogDebug($"CaptureStamp: added stamp at position={transform.position}, rotation={transform.rotation.eulerAngles}, count={currentStampCount}.");
        }

        private void RebuildSetsFromLists()
        {
            observedRendererSet.Clear();
            observedMaterialSet.Clear();

            for (int i = observedRenderers.Count - 1; i >= 0; i--)
            {
                Renderer renderer = observedRenderers[i];
                if (renderer == null)
                {
                    observedRenderers.RemoveAt(i);
                    continue;
                }

                observedRendererSet.Add(renderer);
            }

            for (int i = observedMaterials.Count - 1; i >= 0; i--)
            {
                Material material = observedMaterials[i];
                if (material == null)
                {
                    observedMaterials.RemoveAt(i);
                    continue;
                }

                observedMaterialSet.Add(material);
            }
        }

        private void PushStampStateToCoordinator()
        {
            currentStampCount = sphereStampWorldToLocalMatrices.Count;

            if (!TryGetCoordinator(out StampClipCoordinator coordinator))
            {
                return;
            }

            if (currentStampCount == 0)
            {
                coordinator.ClearSource(this);
            }
            else
            {
                coordinator.SetSourceState(this, sphereStampWorldToLocalMatrices, observedRenderers, observedMaterials);
            }

            hasPendingCoordinatorPublish = false;
            LogDebug($"Published to coordinator: stampCount={currentStampCount}.");
        }

        private void ClearCoordinatorSource()
        {
            if (!TryGetCoordinator(out StampClipCoordinator coordinator))
            {
                return;
            }

            coordinator.ClearSource(this);
            hasPendingCoordinatorPublish = false;
            LogDebug("Cleared coordinator source.");
        }

        private void LogDebug(string message)
        {
            if (!debugLogs)
            {
                return;
            }

            Debug.Log($"[{nameof(StampClipSourcePersistentClipper)}:{name}] {message}", this);
        }
    }
}
