using System;
using System.Collections.Generic;
using EasyButtons;
using Microsoft.MixedReality.GraphicsTools;
using UnityEngine;

namespace VIRTOSHA.ZAnatomy.Clipping
{
    /// <summary>
    /// Records clip stamps from collider intersections and forwards them to StampClipCoordinator.
    /// Targets are registered through collider-based filtering against configured renderer/material lists.
    /// Stamps remain active until <see cref="ResetClipping"/> is called.
    /// </summary>
    [RequireComponent(typeof(Collider), typeof(Rigidbody))]
    [DisallowMultipleComponent]
    [AddComponentMenu("VIRTOSHA/Z-Anatomy/Stamp Clip Source Persistent Clipper")]
    public class StampClipSourcePersistentClipper : StampClipSourceBase
    {
        private const int MaxShaderStamps = 64;

        [Header("Stamping")]
        [SerializeField, Tooltip("Physics layers that can be stamped when intersecting the cutter volume.")]
        private LayerMask targetLayers = ~0;
        [SerializeField, Range(1, MaxShaderStamps), Tooltip("Maximum retained stamps. Hard-limited by shader setting.")]
        private int maxStamps = MaxShaderStamps;

        [SerializeField, Min(0.0f), Tooltip("Minimum position delta required before adding another stamp from motion.")]
        private float minStampTranslation = 0.0025f;

        [SerializeField, Range(0.0f, 180.0f), Tooltip("Minimum rotation delta in degrees required before adding another stamp from motion.")]
        private float minStampRotation = 1.0f;

        [Header("Debug")]
        [SerializeField, Tooltip("Enables StampClipSourcePersistentClipper diagnostic logs in the Unity Console.")]
        private bool debugLogs = true;

        [SerializeField, Tooltip("Adds verbose collision-level logs for overlap filtering and target registration.")]
        private bool debugCollisionLogs;

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

        private void OnEnable()
        {
            EnsureCoordinatorReference();
            RebuildSetsFromLists();
            ValidateColliderConfiguration();
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
            ValidateColliderConfiguration();
        }

        protected override bool ShouldSync()
        {
            return hasPendingCoordinatorPublish;
        }

        protected override void PushUpdateToCoordinator()
        {
            if (!hasPendingCoordinatorPublish)
            {
                return;
            }

            PushStampStateToCoordinator();
        }

        private void OnTriggerEnter(Collider other)
        {
            HandleIntersection(other, forceStamp: true);
        }

        private void OnTriggerStay(Collider other)
        {
            HandleIntersection(other, forceStamp: false);
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

        private void HandleIntersection(Collider other, bool forceStamp)
        {
            if (!TryRegisterColliderAsTarget(other, debugCollisionLogs))
            {
                return;
            }

            if (forceStamp || ShouldCaptureStampPose())
            {
                CaptureStampFromCurrentPose();
            }
        }

        private int CollectCurrentIntersections(bool logDetails, out int registeredCount)
        {
            Collider cutterCollider = GetComponent<Collider>();
            if (cutterCollider == null)
            {
                LogDebugWarning("CollectCurrentIntersections: missing Collider on StampClipSourcePersistentClipper.");
                registeredCount = 0;
                return 0;
            }

            Collider[] overlaps = CollectOverlapsForCollider(cutterCollider);
            registeredCount = 0;
            for (int i = 0; i < overlaps.Length; i++)
            {
                if (TryRegisterColliderAsTarget(overlaps[i], logDetails))
                {
                    registeredCount++;
                }
            }

            if (logDetails)
            {
                LogDebug(
                    $"CollectCurrentIntersections: overlaps={overlaps.Length}, registered={registeredCount}, " +
                    $"trackedRenderers={observedRenderers.Count}, trackedMaterials={observedMaterials.Count}.");
            }

            return overlaps.Length;
        }

        private Collider[] CollectOverlapsForCollider(Collider cutterCollider)
        {
            if (cutterCollider is BoxCollider box)
            {
                Vector3 center = transform.TransformPoint(box.center);
                Vector3 lossyScale = transform.lossyScale;
                Vector3 boxHalfExtents = new Vector3(
                    Mathf.Abs(lossyScale.x) * box.size.x * 0.5f,
                    Mathf.Abs(lossyScale.y) * box.size.y * 0.5f,
                    Mathf.Abs(lossyScale.z) * box.size.z * 0.5f);

                return Physics.OverlapBox(
                    center,
                    boxHalfExtents,
                    transform.rotation,
                    targetLayers.value,
                    QueryTriggerInteraction.Collide);
            }

            if (cutterCollider is SphereCollider sphere)
            {
                Vector3 center = transform.TransformPoint(sphere.center);
                Vector3 lossyScale = transform.lossyScale;
                float maxScale = Mathf.Max(Mathf.Abs(lossyScale.x), Mathf.Abs(lossyScale.y), Mathf.Abs(lossyScale.z));
                float radius = sphere.radius * maxScale;

                return Physics.OverlapSphere(
                    center,
                    radius,
                    targetLayers.value,
                    QueryTriggerInteraction.Collide);
            }

            if (cutterCollider is CapsuleCollider capsule)
            {
                Vector3 lossyScale = transform.lossyScale;
                Vector3 center = transform.TransformPoint(capsule.center);
                Vector3 axisLocal = capsule.direction == 0 ? Vector3.right : capsule.direction == 1 ? Vector3.up : Vector3.forward;
                Vector3 axisWorld = transform.rotation * axisLocal;

                float radiusScale = capsule.direction == 0
                    ? Mathf.Max(Mathf.Abs(lossyScale.y), Mathf.Abs(lossyScale.z))
                    : capsule.direction == 1
                        ? Mathf.Max(Mathf.Abs(lossyScale.x), Mathf.Abs(lossyScale.z))
                        : Mathf.Max(Mathf.Abs(lossyScale.x), Mathf.Abs(lossyScale.y));
                float axisScale = capsule.direction == 0
                    ? Mathf.Abs(lossyScale.x)
                    : capsule.direction == 1
                        ? Mathf.Abs(lossyScale.y)
                        : Mathf.Abs(lossyScale.z);

                float radius = capsule.radius * radiusScale;
                float halfHeight = Mathf.Max(0.0f, (capsule.height * axisScale * 0.5f) - radius);
                Vector3 pointA = center + axisWorld * halfHeight;
                Vector3 pointB = center - axisWorld * halfHeight;

                return Physics.OverlapCapsule(
                    pointA,
                    pointB,
                    radius,
                    targetLayers.value,
                    QueryTriggerInteraction.Collide);
            }

            Bounds bounds = cutterCollider.bounds;
            Vector3 boundsHalfExtents = bounds.extents;
            if (boundsHalfExtents.sqrMagnitude <= Mathf.Epsilon)
            {
                return Array.Empty<Collider>();
            }

            return Physics.OverlapBox(
                bounds.center,
                boundsHalfExtents,
                Quaternion.identity,
                targetLayers.value,
                QueryTriggerInteraction.Collide);
        }

        private bool TryRegisterColliderAsTarget(Collider other, bool logDetails)
        {
            if (other == null)
            {
                if (logDetails)
                {
                    LogDebug("Skipped collider: null.");
                }

                return false;
            }

            if (other.transform.IsChildOf(transform))
            {
                if (logDetails)
                {
                    LogDebug($"Skipped collider '{other.name}': belongs to StampClipSourcePersistentClipper hierarchy.");
                }

                return false;
            }

            int otherLayerMask = 1 << other.gameObject.layer;
            if ((targetLayers.value & otherLayerMask) == 0)
            {
                if (logDetails)
                {
                    LogDebug($"Skipped collider '{other.name}': layer '{LayerMask.LayerToName(other.gameObject.layer)}' not in targetLayers.");
                }

                return false;
            }

            if (renderers.Count == 0 && materials.Count == 0)
            {
                if (logDetails)
                {
                    LogDebug($"Skipped collider '{other.name}': both renderer and material allowlists are empty.");
                }

                return false;
            }

            Renderer targetRenderer = other.GetComponentInParent<Renderer>();
            if (targetRenderer == null)
            {
                if (logDetails)
                {
                    LogDebug($"Skipped collider '{other.name}': no Renderer found in parents.");
                }

                return false;
            }

            return RegisterObservedTargets(targetRenderer, logDetails);
        }

        private bool RegisterObservedTargets(Renderer renderer, bool logDetails)
        {
            if (renderer == null)
            {
                return false;
            }

            bool targetsChanged = false;
            bool rendererIsListed = renderers.Contains(renderer);
            Material[] sharedRendererMaterials = renderer.sharedMaterials;
            List<Material> matchedConfiguredMaterials = null;

            for (int i = 0; i < sharedRendererMaterials.Length; i++)
            {
                Material material = sharedRendererMaterials[i];
                if (material == null || !materials.Contains(material))
                {
                    continue;
                }

                matchedConfiguredMaterials ??= new List<Material>();
                if (!matchedConfiguredMaterials.Contains(material))
                {
                    matchedConfiguredMaterials.Add(material);
                }
            }

            bool hasMaterialMatch = matchedConfiguredMaterials != null && matchedConfiguredMaterials.Count > 0;
            if (!rendererIsListed && !hasMaterialMatch)
            {
                ReleaseRendererMaterialOwnership(renderer);
                if (logDetails)
                {
                    LogDebug($"Renderer rejected: '{renderer.name}' is not in renderers list and has no configured shared material match.");
                }

                return false;
            }

            if (rendererIsListed)
            {
                Material[] rendererMaterials = AcquireRendererMaterials(renderer, instance: true);
                if (observedRendererSet.Add(renderer))
                {
                    observedRenderers.Add(renderer);
                    targetsChanged = true;
                    if (logDetails)
                    {
                        LogDebug($"Renderer accepted via renderers list: '{renderer.name}'.");
                    }
                }

                for (int i = 0; i < rendererMaterials.Length; i++)
                {
                    Material material = rendererMaterials[i];
                    if (material == null)
                    {
                        continue;
                    }

                    if (observedMaterialSet.Add(material))
                    {
                        observedMaterials.Add(material);
                        targetsChanged = true;
                        if (logDetails)
                        {
                            LogDebug($"Material registered from listed renderer: '{material.name}' on renderer '{renderer.name}'.");
                        }
                    }
                }

                if (targetsChanged && sphereStampWorldToLocalMatrices.Count > 0)
                {
                    hasPendingCoordinatorPublish = true;
                }

                return true;
            }

            for (int i = 0; i < matchedConfiguredMaterials.Count; i++)
            {
                Material material = matchedConfiguredMaterials[i];
                if (observedMaterialSet.Add(material))
                {
                    observedMaterials.Add(material);
                    targetsChanged = true;
                    if (logDetails)
                    {
                        LogDebug($"Material accepted via materials list: '{material.name}' on renderer '{renderer.name}'.");
                    }
                }
            }

            if (logDetails)
            {
                LogDebug($"Renderer accepted via material match only: '{renderer.name}', matches={matchedConfiguredMaterials.Count}.");
            }

            if (targetsChanged && sphereStampWorldToLocalMatrices.Count > 0)
            {
                hasPendingCoordinatorPublish = true;
            }

            return true;
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

        private void ValidateColliderConfiguration()
        {
            Collider collider = GetComponent<Collider>();
            if (collider == null)
            {
                LogDebugWarning("StampClipSourcePersistentClipper requires a Collider for collision or overlap detection.");
                return;
            }

            Rigidbody rb = GetComponent<Rigidbody>();
            if (rb == null)
            {
                LogDebugWarning("StampClipSourcePersistentClipper requires a Rigidbody for trigger callbacks.");
                return;
            }

            if (!collider.isTrigger)
            {
                LogDebugWarning(
                    $"Collider '{collider.GetType().Name}' must run in trigger-only mode for {nameof(StampClipSourcePersistentClipper)}. " +
                    "Auto-setting Collider.isTrigger=true.");
                collider.isTrigger = true;
            }

            if (rb.useGravity)
            {
                LogDebugWarning(
                    $"Rigidbody on '{name}' must disable gravity for {nameof(StampClipSourcePersistentClipper)}. " +
                    "Auto-setting Rigidbody.useGravity=false.");
                rb.useGravity = false;
            }

            if (!rb.isKinematic)
            {
                LogDebugWarning(
                    $"Rigidbody on '{name}' must be kinematic for {nameof(StampClipSourcePersistentClipper)}. " +
                    "Auto-setting Rigidbody.isKinematic=true.");
                rb.isKinematic = true;
            }

            if (collider is MeshCollider meshCollider && meshCollider.isTrigger && !meshCollider.convex)
            {
                LogDebugWarning(
                    "MeshCollider trigger requires Convex enabled for reliable trigger callbacks. " +
                    "Auto-setting MeshCollider.convex=true.");
                meshCollider.convex = true;

                if (!meshCollider.convex)
                {
                    Debug.LogError(
                        $"[{nameof(StampClipSourcePersistentClipper)}:{name}] MeshCollider trigger remains non-convex. " +
                        "Stamping depends on trigger callbacks and cannot run in this state. " +
                        "Use a convex MeshCollider or switch to a primitive trigger collider.",
                        this);
                    enabled = false;
                }
            }
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

        private void LogDebugWarning(string message)
        {
            if (!debugLogs)
            {
                return;
            }

            Debug.LogWarning($"[{nameof(StampClipSourcePersistentClipper)}:{name}] {message}", this);
        }
    }
}
