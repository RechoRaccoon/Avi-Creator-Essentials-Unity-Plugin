using System;
using System.Collections.Generic;
using UnityEngine;

namespace AviCreatorEssentials
{
    [Serializable]
    public class PoseBone
    {
        public string path;
        public string name;
        public Quaternion rotation = Quaternion.identity;
        public bool hasPosition;
        public Vector3 position;
    }

    /// <summary>A saved pose. Bones are matched by hierarchy path (then by name) so poses work across similar avatars.</summary>
    public class AvatarPose : ScriptableObject
    {
        public string avatarName;
        public List<PoseBone> bones = new List<PoseBone>();
        public Texture2D thumbnail;
    }
}
