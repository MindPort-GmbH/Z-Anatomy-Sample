using System;
using System.Collections.Generic;
using UnityEngine;

namespace VIRTOSHA.ZAnatomy.Clipping
{
    /// <summary>
    /// Serializable named collection of stamp source GameObjects used by
    /// <see cref="StampClipController"/> for grouped activation.
    /// </summary>
    [Serializable]
    public class StampGroup
    {
        [SerializeField]
        private string groupName = "Default";

        [SerializeField]
        private List<GameObject> stampObjects = new List<GameObject>();

        public string GroupName => groupName;
        public IReadOnlyList<GameObject> StampObjects => stampObjects;

        public void SetGroupName(string value)
        {
            groupName = string.IsNullOrWhiteSpace(value) ? "Default" : value;
        }

        public void SetStampObjects(IEnumerable<GameObject> objects)
        {
            stampObjects = new List<GameObject>();
            if (objects == null)
            {
                return;
            }

            HashSet<GameObject> unique = new HashSet<GameObject>();
            foreach (GameObject stamp in objects)
            {
                if (stamp != null && unique.Add(stamp))
                {
                    stampObjects.Add(stamp);
                }
            }
        }
    }
}
