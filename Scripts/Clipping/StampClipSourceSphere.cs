using System.Collections.Generic;
using UnityEngine;

namespace VIRTOSHA.ZAnatomy.Clipping
{
    /// <summary>
    /// Stamp clip sphere source that publishes its matrix and configured targets to <see cref="StampClipCoordinator"/>.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("VIRTOSHA/Z-Anatomy/Stamp Clip Sphere Source")]
    public class StampClipSourceSphere : StampClipSourceBase
    {
        private readonly List<Matrix4x4> sourceMatrices = new List<Matrix4x4>(1);
        private readonly List<Renderer> targetRenderersBuffer = new List<Renderer>();
        private readonly List<Material> targetMaterialsBuffer = new List<Material>();

        private Matrix4x4 lastPublishedMatrix;
        private int lastPublishedTargetsHash;
        private bool hasPublishedState;

        protected void OnEnable()
        {
            PublishSourceState(force: true);
        }

        protected override void PushUpdateToCoordinator()
        {
            PublishSourceState();
        }

        protected void OnDisable()
        {
            ClearCoordinatorSource();
        }

        protected void OnDestroy()
        {
            ClearCoordinatorSource();
        }

        protected void OnDrawGizmosSelected()
        {
            if (!enabled)
            {
                return;
            }

            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireSphere(Vector3.zero, 0.5f);
        }

        private void PublishSourceState(bool force = false)
        {
            if (!TryGetCoordinator(out StampClipCoordinator coordinator))
            {
                return;
            }

            CollectConfiguredTargets(targetRenderersBuffer, targetMaterialsBuffer);
            if (targetRenderersBuffer.Count == 0 && targetMaterialsBuffer.Count == 0)
            {
                ClearCoordinatorSource();
                return;
            }

            Matrix4x4 currentMatrix = transform.worldToLocalMatrix;
            int targetsHash = ComputeTargetsHash(targetRenderersBuffer, targetMaterialsBuffer);

            if (!force && hasPublishedState && currentMatrix == lastPublishedMatrix && targetsHash == lastPublishedTargetsHash)
            {
                return;
            }

            sourceMatrices.Clear();
            sourceMatrices.Add(currentMatrix);
            coordinator.SetSourceState(this, sourceMatrices, targetRenderersBuffer, targetMaterialsBuffer);

            hasPublishedState = true;
            lastPublishedMatrix = currentMatrix;
            lastPublishedTargetsHash = targetsHash;
        }

        private void ClearCoordinatorSource()
        {
            hasPublishedState = false;
            sourceMatrices.Clear();

            if (!TryGetCoordinator(out StampClipCoordinator coordinator))
            {
                return;
            }

            coordinator.ClearSource(this);
        }

        private static int ComputeTargetsHash(IReadOnlyList<Renderer> renderersList, IReadOnlyList<Material> materialsList)
        {
            int hash = 17;

            for (int i = 0; i < renderersList.Count; i++)
            {
                Renderer renderer = renderersList[i];
                hash = (hash * 31) + (renderer != null ? renderer.GetInstanceID() : 0);
            }

            for (int i = 0; i < materialsList.Count; i++)
            {
                Material material = materialsList[i];
                hash = (hash * 31) + (material != null ? material.GetInstanceID() : 0);
            }

            return hash;
        }
    }
}
