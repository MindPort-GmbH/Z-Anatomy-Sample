using Microsoft.MixedReality.GraphicsTools;
using UnityEngine;

namespace VIRTOSHA.ZAnatomy
{
    /// <summary>
    /// GraphicsTools clipping sphere that only drives _ClipSphereInverseTransform.
    /// Stamp clip globals are exclusively written by StampClipCoordinator.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Scripts/GraphicsTools/BaseShaderClippingSphere")]
    public class BaseShaderClippingSphere : ClippingPrimitive
    {
        private const string ClipSphereInverseTransformProperty = "_ClipSphereInverseTransform";

        private int clipSphereInverseTransformID;
        private Matrix4x4 clipSphereInverseTransform;
        private bool propertyIdsInitialized;

        protected override string Keyword => "_CLIPPING_SPHERE";
        protected override string ClippingSideProperty => "_ClipSphereSide";

        protected void OnDrawGizmosSelected()
        {
            if (!enabled)
            {
                return;
            }

            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireSphere(Vector3.zero, 0.5f);
        }

        protected override void Initialize()
        {
            base.Initialize();
            EnsurePropertyIDs();
        }

        protected override void BeginUpdateShaderProperties()
        {
            EnsurePropertyIDs();
            clipSphereInverseTransform = transform.worldToLocalMatrix;
            base.BeginUpdateShaderProperties();
        }

        protected override void UpdateShaderProperties(MaterialPropertyBlock materialPropertyBlock)
        {
            materialPropertyBlock.SetMatrix(clipSphereInverseTransformID, clipSphereInverseTransform);
        }

        protected override void UpdateShaderProperties(Material material)
        {
            material.SetMatrix(clipSphereInverseTransformID, clipSphereInverseTransform);
        }

        protected new void OnDisable()
        {
            base.OnDisable();
            EnsurePropertyIDs();
            SetSphereEnabled(false);
        }

        private void EnsurePropertyIDs()
        {
            if (propertyIdsInitialized)
            {
                return;
            }

            clipSphereInverseTransformID = Shader.PropertyToID(ClipSphereInverseTransformProperty);
            propertyIdsInitialized = true;
        }

        private void SetSphereEnabled(bool isEnabled)
        {
            Matrix4x4 worldToLocal = isEnabled ? transform.worldToLocalMatrix : Matrix4x4.identity;

            if (renderers != null)
            {
                MaterialPropertyBlock block = new MaterialPropertyBlock();

                for (int i = 0; i < renderers.Count; ++i)
                {
                    Renderer targetRenderer = renderers[i];
                    if (targetRenderer == null)
                    {
                        continue;
                    }

                    targetRenderer.GetPropertyBlock(block);
                    block.SetMatrix(clipSphereInverseTransformID, worldToLocal);
                    targetRenderer.SetPropertyBlock(block);
                }
            }

            if (materials != null)
            {
                for (int i = 0; i < materials.Count; ++i)
                {
                    Material material = materials[i];
                    if (material == null)
                    {
                        continue;
                    }

                    material.SetMatrix(clipSphereInverseTransformID, worldToLocal);
                }
            }
        }
    }
}
