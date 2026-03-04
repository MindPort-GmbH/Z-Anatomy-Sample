using System;
using System.Collections.Generic;
using EasyButtons;
using Microsoft.MixedReality.GraphicsTools;
using UnityEngine;

namespace VIRTOSHA.ZAnatomy
{
    /// <summary>
    /// Records clip stamps from trigger intersections and applies them persistently to touched targets.
    /// Clipping remains active until <see cref="ResetClipping"/> is called.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("VIRTOSHA/Z-Anatomy/Persistent Stamp Clipper")]
    public class PersistentStampClipper : MonoBehaviour
    {
        /// <summary>
        /// Maximum retained stamps. Hard-limited by shader setting.
        /// </summary>
        private const int MaxShaderStamps = 64;
        private const string StampEnabledProperty = "_StampClipEnabled";
        private const string StampCountProperty = "_SphereStampCount";
        private const string StampWorldToLocalProperty = "_SphereStampWorldToLocal";

        [Header("Target Filtering")]
        [SerializeField, Tooltip("Physics layers that can be stamped when intersecting the cutter volume.")]
        private LayerMask targetLayers = ~0;

        [Header("Renders to Clip")]
        [SerializeField, Tooltip("Toggles whether clipping will apply to shared materials or material instances (default) on renderers within the renderers list. This cannot be altered when renderers are already specified.")]
        private bool applyToSharedMaterial;

        /// <summary>
        /// Toggles whether clipping will apply to shared materials or material instances (default) on renderers within the renderers list.
        /// This cannot be altered when renderers are already specified.
        /// </summary>
        /// <remarks>
        /// Applying to shared materials will allow for GPU instancing to batch calls between Renderers
        /// that interact with the same clipping primitives.
        /// </remarks>
        public bool ApplyToSharedMaterial
        {
            get => applyToSharedMaterial;
            set
            {
                if (value != applyToSharedMaterial)
                {
                    if (renderers.Count > 0)
                    {
                        throw new InvalidOperationException("Cannot change material applied to after renderers have been added.");
                    }

                    applyToSharedMaterial = value;
                }
            }
        }

        [SerializeField, Tooltip("The renderer(s) that should be affected by the cutter.")]
        protected List<Renderer> renderers = new List<Renderer>();

        [Header("Materials to Clip")]
        [SerializeField, Tooltip("The material(s) that should be affected by the cutter. Materials on renderers within the renderers list do not need to be added to this list.")]
        protected List<Material> materials = new List<Material>();

        [Header("Stamping")]
        [SerializeField, Range(1, MaxShaderStamps), Tooltip("Maximum retained stamps. Hard-limited by shader setting.")]
        private int maxStamps = MaxShaderStamps;

        [SerializeField, Min(0.0f), Tooltip("Minimum position delta required before adding another stamp from motion.")]
        private float minStampTranslation = 0.0025f;

        [SerializeField, Range(0.0f, 180.0f), Tooltip("Minimum rotation delta in degrees required before adding another stamp from motion.")]
        private float minStampRotation = 1.0f;

        [SerializeField, Tooltip("If enabled, keeps checking trigger overlap each physics step and stamps on movement.")]
        private bool stampOnTriggerStay = true;

        [Header("Debug")]
        [SerializeField, Tooltip("Enables PersistentStampClipper diagnostic logs in the Unity Console.")]
        private bool debugLogs = true;

        [SerializeField, Tooltip("Adds verbose trigger-level logs for overlap filtering and registration.")]
        private bool debugTriggerLogs;

        [SerializeField, Tooltip("Renderers that have been registered as stamp targets in this session.")]
        private List<Renderer> observedRenderers = new List<Renderer>();

        [SerializeField, Tooltip("Materials currently tracked for receiving stamp state updates.")]
        private List<Material> observedMaterials = new List<Material>();

        [SerializeField, Tooltip("Current number of stored stamps applied to the shader.")]
        private int currentStampCount;

        private readonly HashSet<Renderer> observedRendererSet = new HashSet<Renderer>();
        private readonly HashSet<Material> observedMaterialSet = new HashSet<Material>();
        private readonly HashSet<Renderer> instanceMaterialOwners = new HashSet<Renderer>();
        private readonly List<Matrix4x4> sphereStampWorldToLocalMatrices = new List<Matrix4x4>();
        private readonly Matrix4x4[] sphereStampWorldToLocalBuffer = new Matrix4x4[MaxShaderStamps];

        private MaterialPropertyBlock materialPropertyBlock;
        private int stampEnabledID;
        private int stampCountID;
        private int stampWorldToLocalID;
        private bool hasLastStampPose;
        private Vector3 lastStampPosition;
        private Quaternion lastStampRotation;

        public IReadOnlyList<Material> AffectedMaterials => observedMaterials;

        private void Awake()
        {
            EnsureInitialized();
        }

        private void OnEnable()
        {
            EnsureInitialized();
            RebuildSetsFromLists();
            ValidateCutterColliderConfiguration();
            PushStampStateToTargets();
        }

        private void OnDestroy()
        {
            ClearAffectedTargets();
        }

        private void OnValidate()
        {
            maxStamps = Mathf.Clamp(maxStamps, 1, MaxShaderStamps);
            minStampTranslation = Mathf.Max(0.0f, minStampTranslation);
            minStampRotation = Mathf.Clamp(minStampRotation, 0.0f, 180.0f);
        }

        private void OnTriggerEnter(Collider other)
        {
            HandleIntersection(other, forceStamp: true);
        }

        private void OnTriggerStay(Collider other)
        {
            if (stampOnTriggerStay)
            {
                HandleIntersection(other, forceStamp: false);
            }
        }

        private void FixedUpdate()
        {
            if (!stampOnTriggerStay)
            {
                return;
            }

            Collider cutterCollider = GetComponent<Collider>();
            if (cutterCollider == null || cutterCollider.isTrigger)
            {
                return;
            }

            int registeredCount;
            CollectCurrentIntersections(logDetails: false, out registeredCount);

            if (registeredCount > 0 && ShouldCaptureStampPose())
            {
                CaptureStampFromCurrentPose(force: true);
            }
        }

        [Button]
        public void ResetClipping()
        {
            sphereStampWorldToLocalMatrices.Clear();
            currentStampCount = 0;
            hasLastStampPose = false;
            PushStampStateToTargets();
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
            if (!TryRegisterColliderAsTarget(other, debugTriggerLogs))
            {
                return;
            }

            if (forceStamp || ShouldCaptureStampPose())
            {
                CaptureStampFromCurrentPose(force: true);
            }
        }

        private int CollectCurrentIntersections(bool logDetails, out int registeredCount)
        {
            Collider cutterCollider = GetComponent<Collider>();
            if (cutterCollider == null)
            {
                LogDebugWarning("CollectCurrentIntersections: missing Collider on PersistentStampClipper.");
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
                return System.Array.Empty<Collider>();
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
                    LogDebug($"Skipped collider '{other.name}': belongs to PersistentStampClipper hierarchy.");
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

            bool rendererIsListed = renderers.Contains(renderer);
            Material[] sharedRendererMaterials = renderer.sharedMaterials;
            List<Material> matchedConfiguredMaterials = null;

            for (int i = 0; i < sharedRendererMaterials.Length; i++)
            {
                Material material = sharedRendererMaterials[i];
                if (material == null)
                {
                    continue;
                }

                if (!materials.Contains(material))
                {
                    continue;
                }

                if (matchedConfiguredMaterials == null)
                {
                    matchedConfiguredMaterials = new List<Material>();
                }

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
                        if (logDetails)
                        {
                            LogDebug($"Material registered from listed renderer: '{material.name}' on renderer '{renderer.name}'.");
                        }
                    }
                }

                ApplyStampState(renderer);
                return true;
            }

            for (int i = 0; i < matchedConfiguredMaterials.Count; i++)
            {
                Material material = matchedConfiguredMaterials[i];
                if (observedMaterialSet.Add(material))
                {
                    observedMaterials.Add(material);
                    if (logDetails)
                    {
                        LogDebug($"Material accepted via materials list: '{material.name}' on renderer '{renderer.name}'.");
                    }
                }

                ApplyStampState(material);
            }

            if (logDetails)
            {
                LogDebug($"Renderer accepted via material match only: '{renderer.name}', matches={matchedConfiguredMaterials.Count}.");
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

        private void CaptureStampFromCurrentPose(bool force)
        {
            if (!force && !ShouldCaptureStampPose())
            {
                return;
            }

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

            PushStampStateToTargets();
            LogDebug($"CaptureStamp: added stamp at position={transform.position}, rotation={transform.rotation.eulerAngles}, count={currentStampCount}.");
        }

        private void EnsureInitialized()
        {
            if (materialPropertyBlock == null)
            {
                materialPropertyBlock = new MaterialPropertyBlock();
            }

            if (stampEnabledID == 0)
            {
                stampEnabledID = Shader.PropertyToID(StampEnabledProperty);
                stampCountID = Shader.PropertyToID(StampCountProperty);
                stampWorldToLocalID = Shader.PropertyToID(StampWorldToLocalProperty);
            }
        }

        private void ValidateCutterColliderConfiguration()
        {
            Collider cutterCollider = GetComponent<Collider>();
            if (cutterCollider == null)
            {
                LogDebugWarning("PersistentStampClipper requires a Collider for trigger or overlap detection.");
                return;
            }

            if (cutterCollider is MeshCollider meshCollider && meshCollider.isTrigger && !meshCollider.convex)
            {
                LogDebugWarning("MeshCollider trigger requires Convex enabled. Trigger callbacks may not fire.");
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

        private void PushStampStateToTargets()
        {
            EnsureInitialized();
            currentStampCount = sphereStampWorldToLocalMatrices.Count;
            ApplyGlobalStampState();

            for (int i = observedRenderers.Count - 1; i >= 0; i--)
            {
                Renderer renderer = observedRenderers[i];
                if (renderer == null)
                {
                    observedRenderers.RemoveAt(i);
                    continue;
                }

                ApplyStampState(renderer);
            }

            for (int i = observedMaterials.Count - 1; i >= 0; i--)
            {
                Material material = observedMaterials[i];
                if (material == null)
                {
                    observedMaterials.RemoveAt(i);
                    continue;
                }

                ApplyStampState(material);
            }

            LogDebug($"PushStampStateToTargets: stampCount={currentStampCount}, renderers={observedRenderers.Count}, materials={observedMaterials.Count}.");
        }

        private void ApplyStampState(Renderer renderer)
        {
            renderer.GetPropertyBlock(materialPropertyBlock);
            WriteStampState(materialPropertyBlock);
            renderer.SetPropertyBlock(materialPropertyBlock);
        }

        private void ApplyStampState(Material material)
        {
            int clampedCount = PrepareStampBuffer();
            if (material == null)
            {
                return;
            }

            // Stamp uniforms are applied globally. Apply to material only when these properties exist.
            if (material.HasProperty(stampEnabledID))
            {
                material.SetFloat(stampEnabledID, clampedCount > 0 ? 1.0f : 0.0f);
            }

            if (material.HasProperty(stampCountID))
            {
                material.SetFloat(stampCountID, clampedCount);
            }

            if (material.HasProperty(stampWorldToLocalID))
            {
                material.SetMatrixArray(stampWorldToLocalID, sphereStampWorldToLocalBuffer);
            }
        }

        private void WriteStampState(MaterialPropertyBlock block)
        {
            int clampedCount = PrepareStampBuffer();
            block.SetFloat(stampEnabledID, clampedCount > 0 ? 1.0f : 0.0f);
            block.SetFloat(stampCountID, clampedCount);
            block.SetMatrixArray(stampWorldToLocalID, sphereStampWorldToLocalBuffer);
        }

        private int PrepareStampBuffer()
        {
            int clampedCount = Mathf.Clamp(currentStampCount, 0, MaxShaderStamps);

            for (int i = 0; i < MaxShaderStamps; i++)
            {
                sphereStampWorldToLocalBuffer[i] = Matrix4x4.identity;
            }

            for (int i = 0; i < clampedCount; i++)
            {
                sphereStampWorldToLocalBuffer[i] = sphereStampWorldToLocalMatrices[i];
            }

            return clampedCount;
        }

        private void ApplyGlobalStampState()
        {
            int clampedCount = PrepareStampBuffer();
            Shader.SetGlobalFloat(stampEnabledID, clampedCount > 0 ? 1.0f : 0.0f);
            Shader.SetGlobalFloat(stampCountID, clampedCount);
            Shader.SetGlobalMatrixArray(stampWorldToLocalID, sphereStampWorldToLocalBuffer);

            LogDebug($"ApplyGlobalStampState: enabled={(clampedCount > 0 ? 1 : 0)}, count={clampedCount}.");
        }

        private void LogDebug(string message)
        {
            if (!debugLogs)
            {
                return;
            }

            Debug.Log($"[PersistentStampClipper:{name}] {message}", this);
        }

        private void LogDebugWarning(string message)
        {
            if (!debugLogs)
            {
                return;
            }

            Debug.LogWarning($"[PersistentStampClipper:{name}] {message}", this);
        }
    }
}
