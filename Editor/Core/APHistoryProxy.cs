using UnityEngine;

namespace AviCreatorEssentials
{
    /// <summary>
    /// Hidden object recorded with Unity's Undo system so Ctrl+Z / Ctrl+Y (and Edit > Undo) drive the painter's own
    /// history. Its value is the sequence number of the last applied painter step.
    /// </summary>
    public class APHistoryProxy : ScriptableObject
    {
        public int appliedSequence;
    }
}
