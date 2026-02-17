using System.Collections.Generic;
using EasyButtons;
using UnityEngine;

namespace VIRTOSHA.ZAnatomy
{
    /// <summary>
    /// Records wedge clip stamps from trigger intersections and applies them persistently to touched targets.
    /// Clipping remains active until <see cref="ResetClipping"/> is called.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("VIRTOSHA/Z-Anatomy/Stamp Cut")]
    public class StampCut : MonoBehaviour
    {
        private const int MaxShaderStamps = 64;
        private const string StampEnabledProperty = "_StampCutEnabled";
        private const string StampCountProperty = "_WedgeStampCount";
        private const string StampInverseProperty = "_WedgeStampInverse";

        [Header("Target Filtering")]
        [SerializeField, Tooltip("Physics layers that can be stamped when intersecting the cutter volume.")]
        private LayerMask targetLayers = ~0;

        [SerializeField, Tooltip("Only materials whose shader name exactly matches this value are affected. Leave empty to allow any shader.")]
        private string requiredShaderName = "Shader Graphs/BaseShaderStampCut";

        [Header("Stamping")]
        [SerializeField, Range(1, MaxShaderStamps), Tooltip("Maximum retained wedge stamps. Hard-limited by shader support.")]
        private int maxStamps = MaxShaderStamps;

        [SerializeField, Min(0.0f), Tooltip("Minimum position delta required before adding another stamp from motion.")]
        private float minStampTranslation = 0.0025f;

        [SerializeField, Range(0.0f, 180.0f), Tooltip("Minimum rotation delta in degrees required before adding another stamp from motion.")]
        private float minStampRotation = 1.0f;

        [SerializeField, Tooltip("If enabled, keeps checking trigger overlap each physics step and stamps on movement.")]
        private bool stampOnTriggerStay = true;

        [Header("Debug")]
        [SerializeField, Tooltip("Enables StampCut diagnostic logs in the Unity Console.")]
        private bool debugLogs = true;

        [SerializeField, Tooltip("Adds verbose trigger-level logs for overlap filtering and registration.")]
        private bool debugTriggerLogs;

        [Header("Observed Targets")]
        [SerializeField, Tooltip("Renderers that have been registered as stamp targets in this session.")]
        private List<Renderer> affectedRenderers = new List<Renderer>();

        [SerializeField, Tooltip("Materials currently tracked for receiving stamp state updates.")]
        private List<Material> affectedMaterials = new List<Material>();

        [SerializeField, Tooltip("Current number of stored wedge stamps applied to the shader.")]
        private int currentStampCount;

        private readonly HashSet<Renderer> affectedRendererSet = new HashSet<Renderer>();
        private readonly HashSet<Material> affectedMaterialSet = new HashSet<Material>();
        private readonly List<Matrix4x4> wedgeStampInverses = new List<Matrix4x4>();
        private readonly Matrix4x4[] wedgeStampInverseBuffer = new Matrix4x4[MaxShaderStamps];

        private MaterialPropertyBlock materialPropertyBlock;
        private int stampEnabledID;
        private int stampCountID;
        private int stampInverseID;
        private bool hasLastStampPose;
        private Vector3 lastStampPosition;
        private Quaternion lastStampRotation;

        public IReadOnlyList<Material> AffectedMaterials => affectedMaterials;

        private void Awake()
        {
            EnsureInitialized();
        }

        private void OnEnable()
        {
            EnsureInitialized();
            RebuildSetsFromLists();
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

        [Button]
        public void StampNow()
        {
            EnsureInitialized();
            int beforeRendererCount = affectedRenderers.Count;
            int beforeMaterialCount = affectedMaterials.Count;
            int overlapCount = CollectCurrentIntersections(logDetails: true);

            LogDebug(
                $"StampNow: overlaps={overlapCount}, newRenderers={affectedRenderers.Count - beforeRendererCount}, " +
                $"newMaterials={affectedMaterials.Count - beforeMaterialCount}, existingStamps={currentStampCount}.");

            CaptureStampFromCurrentPose(force: true);

            LogDebug($"StampNow complete: currentStampCount={currentStampCount}.");
        }

        [Button]
        public void ResetClipping()
        {
            wedgeStampInverses.Clear();
            currentStampCount = 0;
            hasLastStampPose = false;
            PushStampStateToTargets();
            LogDebug("ResetClipping: cleared all stored wedge stamps.");
        }

        [Button]
        public void ClearAffectedTargets()
        {
            ResetClipping();
            affectedRendererSet.Clear();
            affectedMaterialSet.Clear();
            affectedRenderers.Clear();
            affectedMaterials.Clear();
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

        private int CollectCurrentIntersections(bool logDetails)
        {
            BoxCollider box = GetComponent<BoxCollider>();
            if (box == null)
            {
                LogDebugWarning("CollectCurrentIntersections: missing BoxCollider on StampCut.");
                return 0;
            }

            Vector3 center = transform.TransformPoint(box.center);
            Vector3 lossyScale = transform.lossyScale;
            Vector3 halfExtents = new Vector3(
                Mathf.Abs(lossyScale.x) * box.size.x * 0.5f,
                Mathf.Abs(lossyScale.y) * box.size.y * 0.5f,
                Mathf.Abs(lossyScale.z) * box.size.z * 0.5f);

            Collider[] overlaps = Physics.OverlapBox(
                center,
                halfExtents,
                transform.rotation,
                targetLayers.value,
                QueryTriggerInteraction.Collide);

            int registeredCount = 0;
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
                    $"trackedRenderers={affectedRenderers.Count}, trackedMaterials={affectedMaterials.Count}.");
            }

            return overlaps.Length;
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
                    LogDebug($"Skipped collider '{other.name}': belongs to StampCut hierarchy.");
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

            Renderer targetRenderer = other.GetComponentInParent<Renderer>();
            if (targetRenderer == null)
            {
                if (logDetails)
                {
                    LogDebug($"Skipped collider '{other.name}': no Renderer found in parents.");
                }

                return false;
            }

            return RegisterAffectedRenderer(targetRenderer, logDetails);
        }

        private bool RegisterAffectedRenderer(Renderer renderer, bool logDetails)
        {
            if (renderer == null)
            {
                return false;
            }

            Material[] sharedMaterials = renderer.sharedMaterials;
            bool hasSupportedMaterial = false;

            for (int i = 0; i < sharedMaterials.Length; i++)
            {
                Material material = sharedMaterials[i];
                if (!IsSupportedMaterial(material))
                {
                    if (logDetails)
                    {
                        string shaderName = material != null && material.shader != null ? material.shader.name : "<null shader>";
                        string materialName = material != null ? material.name : "<null material>";
                        LogDebug($"Material rejected: '{materialName}' shader='{shaderName}', expected='{requiredShaderName}'.");
                    }

                    continue;
                }

                hasSupportedMaterial = true;
                if (affectedMaterialSet.Add(material))
                {
                    affectedMaterials.Add(material);
                    if (logDetails)
                    {
                        LogDebug($"Material accepted: '{material.name}' on renderer '{renderer.name}'.");
                    }
                }
            }

            if (!hasSupportedMaterial)
            {
                if (logDetails)
                {
                    LogDebug($"Renderer rejected: '{renderer.name}' has no supported materials.");
                }

                return false;
            }

            if (affectedRendererSet.Add(renderer))
            {
                affectedRenderers.Add(renderer);
                if (logDetails)
                {
                    LogDebug($"Renderer registered: '{renderer.name}'.");
                }
            }

            ApplyStampState(renderer);
            return true;
        }

        private bool IsSupportedMaterial(Material material)
        {
            if (material == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(requiredShaderName))
            {
                return true;
            }

            Shader shader = material.shader;
            return shader != null && shader.name == requiredShaderName;
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

            if (wedgeStampInverses.Count >= maxStamps)
            {
                wedgeStampInverses.RemoveAt(0);
                LogDebug($"CaptureStamp: reached maxStamps={maxStamps}, removed oldest stamp.");
            }

            wedgeStampInverses.Add(transform.worldToLocalMatrix);
            currentStampCount = wedgeStampInverses.Count;
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
                stampInverseID = Shader.PropertyToID(StampInverseProperty);
            }
        }

        private void RebuildSetsFromLists()
        {
            affectedRendererSet.Clear();
            affectedMaterialSet.Clear();

            for (int i = affectedRenderers.Count - 1; i >= 0; i--)
            {
                Renderer renderer = affectedRenderers[i];
                if (renderer == null)
                {
                    affectedRenderers.RemoveAt(i);
                    continue;
                }

                affectedRendererSet.Add(renderer);
            }

            for (int i = affectedMaterials.Count - 1; i >= 0; i--)
            {
                Material material = affectedMaterials[i];
                if (material == null)
                {
                    affectedMaterials.RemoveAt(i);
                    continue;
                }

                affectedMaterialSet.Add(material);
            }
        }

        private void PushStampStateToTargets()
        {
            EnsureInitialized();
            currentStampCount = wedgeStampInverses.Count;
            ApplyGlobalStampState();

            for (int i = affectedRenderers.Count - 1; i >= 0; i--)
            {
                Renderer renderer = affectedRenderers[i];
                if (renderer == null)
                {
                    affectedRenderers.RemoveAt(i);
                    continue;
                }

                ApplyStampState(renderer);
            }

            for (int i = affectedMaterials.Count - 1; i >= 0; i--)
            {
                Material material = affectedMaterials[i];
                if (material == null)
                {
                    affectedMaterials.RemoveAt(i);
                    continue;
                }

                ApplyStampState(material);
            }

            LogDebug($"PushStampStateToTargets: stampCount={currentStampCount}, renderers={affectedRenderers.Count}, materials={affectedMaterials.Count}.");
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

            if (material.HasProperty(stampInverseID))
            {
                material.SetMatrixArray(stampInverseID, wedgeStampInverseBuffer);
            }
        }

        private void WriteStampState(MaterialPropertyBlock block)
        {
            int clampedCount = PrepareStampBuffer();
            block.SetFloat(stampEnabledID, clampedCount > 0 ? 1.0f : 0.0f);
            block.SetFloat(stampCountID, clampedCount);
            block.SetMatrixArray(stampInverseID, wedgeStampInverseBuffer);
        }

        private int PrepareStampBuffer()
        {
            int clampedCount = Mathf.Clamp(currentStampCount, 0, MaxShaderStamps);

            for (int i = 0; i < MaxShaderStamps; i++)
            {
                wedgeStampInverseBuffer[i] = Matrix4x4.identity;
            }

            for (int i = 0; i < clampedCount; i++)
            {
                wedgeStampInverseBuffer[i] = wedgeStampInverses[i];
            }

            return clampedCount;
        }

        private void ApplyGlobalStampState()
        {
            int clampedCount = PrepareStampBuffer();
            Shader.SetGlobalFloat(stampEnabledID, clampedCount > 0 ? 1.0f : 0.0f);
            Shader.SetGlobalFloat(stampCountID, clampedCount);
            Shader.SetGlobalMatrixArray(stampInverseID, wedgeStampInverseBuffer);

            LogDebug($"ApplyGlobalStampState: enabled={(clampedCount > 0 ? 1 : 0)}, count={clampedCount}.");
        }

        private void LogDebug(string message)
        {
            if (!debugLogs)
            {
                return;
            }

            Debug.Log($"[StampCut:{name}] {message}", this);
        }

        private void LogDebugWarning(string message)
        {
            if (!debugLogs)
            {
                return;
            }

            Debug.LogWarning($"[StampCut:{name}] {message}", this);
        }
    }
}
