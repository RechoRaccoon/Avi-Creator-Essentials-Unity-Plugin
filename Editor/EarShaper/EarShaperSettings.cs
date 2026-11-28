using System;
using System.Collections.Generic;
using UnityEngine;

namespace AviCreatorEssentials
{
    /// <summary>One named piece of the ears with its own selection and its own settings (the ear itself, the inner fluff, ...).</summary>
    [Serializable]
    public class EarPart
    {
        public string name = "Ear";
        /// <summary>0 = ear shell, 1 = inner fluff (follows the ear shell, with its own length / width / inset).</summary>
        public int kind;
        public Color color = new Color(1f, 0.55f, 0.1f, 1f);
        public float featherCm = 1.5f;

        // ear shell
        public float length = 3f, width = 1f, thick = 1f, bunnify = 0.8f, tipRound = 0.65f, baseLock = 0.2f, cup = 0.15f;
        public float bendDeg, twistDeg, outDeg, fwdDeg;

        // inner fluff
        public bool fluffAuto = true;
        public float fStart = 0.05f, fEnd = 0.8f;   // where the fluff sits along the ear (0 = base, 1 = tip), when not auto
        public float fStretch = 0.5f;               // 0 = keeps its own length, 1 = stretches with the ear
        public float fWidth = 1f, fFollow = 1f, fThick = 1f, fInsetCm;

        public void ResetIdentity()
        {
            length = 1f; width = 1f; thick = 1f; bunnify = 0f; tipRound = 0.65f; baseLock = 0.2f; cup = 0f;
            bendDeg = 0f; twistDeg = 0f; outDeg = 0f; fwdDeg = 0f;
            fluffAuto = true; fStart = 0.05f; fEnd = 0.8f; fStretch = 1f; fWidth = 1f; fFollow = 1f; fThick = 1f; fInsetCm = 0f;
        }

        public void ResetDefault()
        {
            ResetIdentity();
            length = 3f; bunnify = 0.8f; cup = 0.15f; fStretch = 0.5f;
        }

        public EarPart Clone() { return JsonUtility.FromJson<EarPart>(JsonUtility.ToJson(this)); }
    }

    /// <summary>What was done to one mesh, so it can be reopened later.</summary>
    [Serializable]
    public class EarShaperTargetData
    {
        public string rendererName;
        public Mesh original;     // the untouched mesh the shaping starts from
        public Mesh result;       // the mesh that was put on the avatar
        public int[] partOf;      // per vertex of the original mesh: which part selected it, or -1
        public int[] sculptIdx;   // vertices that were hand sculpted
        public Vector3[] sculptVal;
    }

    /// <summary>Saved next to the generated meshes. It is only used by the Ear Shaper to resume editing and is never part of the avatar.</summary>
    public class EarShaperSettings : ScriptableObject
    {
        public List<EarPart> parts = new List<EarPart>();
        public List<EarShaperTargetData> targets = new List<EarShaperTargetData>();
        public int output;
        public string shapeName = "BunnyEars";
        public bool defaultOn = true;
    }
}
