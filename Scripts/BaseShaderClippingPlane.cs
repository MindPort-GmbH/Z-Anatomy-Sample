using Microsoft.MixedReality.GraphicsTools;
using UnityEngine;

namespace VIRTOSHA.ZAnatomy
{
    /// <summary>
    /// Clipping plane compatible with both GraphicsTools clipping shaders and
    /// Z-Anatomy shader graphs that use _PlanePosition/_PlaneNormal/_PlaneEnabled.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Scripts/GraphicsTools/BaseShaderClippingPlane")]
    public class BaseShaderClippingPlane : ClippingPrimitive
    {
        private const string ClipPlaneProperty = "_ClipPlane";
        private const string PlanePositionProperty = "_PlanePosition";
        private const string PlaneNormalProperty = "_PlaneNormal";
        private const string PlaneEnabledProperty = "_PlaneEnabled";

        private int clipPlaneID;
        private int planePositionID;
        private int planeNormalID;
        private int planeEnabledID;

        private Vector4 clipPlane;
        private Vector3 planePosition;
        private Vector3 planeNormal;

        private bool propertyIdsInitialized;

        protected override string Keyword => "_CLIPPING_PLANE";

        protected override string ClippingSideProperty => "_ClipPlaneSide";

        protected void OnDrawGizmosSelected()
        {
            if (!enabled)
            {
                return;
            }

            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(1.0f, 0.0f, 1.0f));
            Gizmos.DrawLine(Vector3.zero, Vector3.up * -0.5f);
        }

        protected override void Initialize()
        {
            base.Initialize();
            EnsurePropertyIDs();
        }

        protected override void BeginUpdateShaderProperties()
        {
            EnsurePropertyIDs();

            Vector3 normal = transform.up;
            float sideSign = (float)ClippingSide;

            clipPlane = new Vector4(normal.x, normal.y, normal.z, Vector3.Dot(normal, transform.position));
            planePosition = transform.position;
            planeNormal = normal * sideSign;

            base.BeginUpdateShaderProperties();
        }

        protected override void UpdateShaderProperties(MaterialPropertyBlock materialPropertyBlock)
        {
            materialPropertyBlock.SetVector(clipPlaneID, clipPlane);
            materialPropertyBlock.SetVector(planePositionID, planePosition);
            materialPropertyBlock.SetVector(planeNormalID, planeNormal);
            materialPropertyBlock.SetFloat(planeEnabledID, 1.0f);
        }

        protected override void UpdateShaderProperties(Material material)
        {
            material.SetVector(clipPlaneID, clipPlane);
            material.SetVector(planePositionID, planePosition);
            material.SetVector(planeNormalID, planeNormal);
            material.SetFloat(planeEnabledID, 1.0f);
        }

        // Explicitly clear _PlaneEnabled so shader graph clipping turns off when this component is disabled.
        protected new void OnDisable()
        {
            base.OnDisable();
            EnsurePropertyIDs();
            SetPlaneEnabled(false);
        }

        private void EnsurePropertyIDs()
        {
            if (propertyIdsInitialized)
            {
                return;
            }

            clipPlaneID = Shader.PropertyToID(ClipPlaneProperty);
            planePositionID = Shader.PropertyToID(PlanePositionProperty);
            planeNormalID = Shader.PropertyToID(PlaneNormalProperty);
            planeEnabledID = Shader.PropertyToID(PlaneEnabledProperty);

            propertyIdsInitialized = true;
        }

        private void SetPlaneEnabled(bool isEnabled)
        {
            float enabledValue = isEnabled ? 1.0f : 0.0f;

            if (renderers != null)
            {
                var block = new MaterialPropertyBlock();

                for (int i = 0; i < renderers.Count; ++i)
                {
                    Renderer targetRenderer = renderers[i];
                    if (targetRenderer == null)
                    {
                        continue;
                    }

                    targetRenderer.GetPropertyBlock(block);
                    block.SetFloat(planeEnabledID, enabledValue);
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

                    material.SetFloat(planeEnabledID, enabledValue);
                }
            }
        }
    }
}
