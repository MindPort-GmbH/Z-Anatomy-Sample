using System.Collections.Generic;
using EasyButtons;
using UnityEngine;

namespace VIRTOSHA.ZAnatomy
{
    /// <summary>
    /// Deprecated compatibility component.
    /// It no longer writes stamp shader properties and only forwards active source matrices to StampClipCoordinator.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("VIRTOSHA/Z-Anatomy/Multi Sphere Stamp Clipper (Deprecated)")]
    public class MultiSphereStampClipper : MonoBehaviour
    {
        [SerializeField, Tooltip("Global stamp clip coordinator.")]
        private StampClipCoordinator stampClipCoordinator;

        [SerializeField, Tooltip("Stamp source objects.")]
        private List<GameObject> stampSourceObjects = new List<GameObject>();

        [SerializeField, Tooltip("Logs a one-time warning when this legacy component is used.")]
        private bool logDeprecationWarning = true;

        private bool hasLoggedDeprecationWarning;

        private void OnEnable()
        {
            LogDeprecationWarningOnce();
            PublishSourceMatrices();
        }

        private void OnDisable()
        {
            ClearSource();
        }

        private void OnDestroy()
        {
            ClearSource();
        }

        [Button]
        public void RefreshNow()
        {
            LogDeprecationWarningOnce();
            PublishSourceMatrices();
        }

        public void RefreshFromStampObjects(IEnumerable<GameObject> sources)
        {
            LogDeprecationWarningOnce();

            stampSourceObjects.Clear();
            if (sources != null)
            {
                HashSet<GameObject> unique = new HashSet<GameObject>();
                foreach (GameObject source in sources)
                {
                    if (source != null && unique.Add(source))
                    {
                        stampSourceObjects.Add(source);
                    }
                }
            }

            PublishSourceMatrices();
        }

        private void EnsureCoordinatorReference()
        {
            if (stampClipCoordinator == null)
            {
                stampClipCoordinator = FindAnyObjectByType<StampClipCoordinator>();
            }
        }

        private void PublishSourceMatrices()
        {
            EnsureCoordinatorReference();
            if (stampClipCoordinator == null)
            {
                return;
            }

            List<Matrix4x4> matrices = new List<Matrix4x4>();
            for (int i = 0; i < stampSourceObjects.Count; i++)
            {
                GameObject source = stampSourceObjects[i];
                if (source == null || !source.activeInHierarchy)
                {
                    continue;
                }

                BaseShaderClippingSphere[] spheres = source.GetComponentsInChildren<BaseShaderClippingSphere>(false);
                for (int sphereIndex = 0; sphereIndex < spheres.Length; sphereIndex++)
                {
                    BaseShaderClippingSphere sphere = spheres[sphereIndex];
                    if (sphere == null || !sphere.isActiveAndEnabled)
                    {
                        continue;
                    }

                    matrices.Add(sphere.transform.worldToLocalMatrix);
                }
            }

            if (matrices.Count == 0)
            {
                stampClipCoordinator.ClearSource(this);
            }
            else
            {
                stampClipCoordinator.SetSourceMatrices(this, matrices);
            }
        }

        private void ClearSource()
        {
            EnsureCoordinatorReference();
            if (stampClipCoordinator == null)
            {
                return;
            }

            stampClipCoordinator.ClearSource(this);
        }

        private void LogDeprecationWarningOnce()
        {
            if (!logDeprecationWarning || hasLoggedDeprecationWarning)
            {
                return;
            }

            Debug.LogWarning(
                $"[{nameof(MultiSphereStampClipper)}:{name}] Deprecated: this component no longer writes shader properties. Use {nameof(StampClipCoordinator)} directly.",
                this);
            hasLoggedDeprecationWarning = true;
        }
    }
}
