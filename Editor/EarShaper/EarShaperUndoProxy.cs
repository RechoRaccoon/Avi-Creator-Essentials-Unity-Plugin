using UnityEngine;

namespace AviCreatorEssentials
{
    /// <summary>
    /// A tiny hidden object the Ear Shaper registers with Unity's Undo system. Every undoable action bumps its revision,
    /// so Ctrl+Z / Edit › Undo / Redo work from anywhere and the Ear Shaper restores the matching step.
    /// </summary>
    public class EarShaperUndoProxy : ScriptableObject
    {
        public int rev;
    }
}
