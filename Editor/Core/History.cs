using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AviCreatorEssentials
{
    public interface IHistoryEntry
    {
        string Name { get; }
        int Sequence { get; set; }
        long Bytes { get; }
        void Undo();
        void Redo();
        /// <param name="applied">True when the entry is dropped while its change is in effect.</param>
        void Dispose(bool applied);
    }

    /// <summary>Swaps a render texture with a snapshot taken before the change. Undo and redo are the same swap.</summary>
    public class PixelEntry : IHistoryEntry
    {
        readonly RenderTexture m_Target;
        RenderTexture m_Snapshot;
        readonly Action m_OnChanged;

        public string Name { get; }
        public int Sequence { get; set; }
        public long Bytes => APUtil.EstimateBytes(m_Snapshot);

        public PixelEntry(string name, RenderTexture target, RenderTexture snapshot, Action onChanged)
        {
            Name = name;
            m_Target = target;
            m_Snapshot = snapshot;
            m_OnChanged = onChanged;
        }

        void Swap()
        {
            if (m_Target == null || m_Snapshot == null) return;
            var tmp = RenderTexture.GetTemporary(m_Target.descriptor);
            APUtil.Copy(m_Target, tmp);
            APUtil.Copy(m_Snapshot, m_Target);
            APUtil.Copy(tmp, m_Snapshot);
            RenderTexture.ReleaseTemporary(tmp);
            m_OnChanged?.Invoke();
        }

        public void Undo() => Swap();
        public void Redo() => Swap();
        public void Dispose(bool applied) => APUtil.Release(ref m_Snapshot);
    }

    /// <summary>A change described by two delegates.</summary>
    public class ActionEntry : IHistoryEntry
    {
        readonly Action m_Undo;
        readonly Action m_Redo;
        readonly Action<bool> m_Dispose;

        public string Name { get; }
        public int Sequence { get; set; }
        public long Bytes { get; set; }

        public ActionEntry(string name, Action undo, Action redo, Action<bool> dispose = null, long bytes = 0)
        {
            Name = name;
            m_Undo = undo;
            m_Redo = redo;
            m_Dispose = dispose;
            Bytes = bytes;
        }

        public void Undo() => m_Undo?.Invoke();
        public void Redo() => m_Redo?.Invoke();
        public void Dispose(bool applied) => m_Dispose?.Invoke(applied);
    }

    /// <summary>Several entries applied and reverted together.</summary>
    public class CompoundEntry : IHistoryEntry
    {
        readonly List<IHistoryEntry> m_Entries;

        public string Name { get; }
        public int Sequence { get; set; }

        public long Bytes
        {
            get
            {
                long b = 0;
                foreach (var e in m_Entries) b += e.Bytes;
                return b;
            }
        }

        public CompoundEntry(string name, List<IHistoryEntry> entries)
        {
            Name = name;
            m_Entries = entries;
        }

        public void Undo()
        {
            for (int i = m_Entries.Count - 1; i >= 0; i--) m_Entries[i].Undo();
        }

        public void Redo()
        {
            foreach (var e in m_Entries) e.Redo();
        }

        public void Dispose(bool applied)
        {
            foreach (var e in m_Entries) e.Dispose(applied);
        }
    }

    /// <summary>
    /// The painter's undo history. Every step is mirrored by a record on a hidden proxy object in Unity's own Undo
    /// stack, so the normal editor shortcuts and menu items undo and redo painting.
    /// </summary>
    public class History : IDisposable
    {
        readonly List<IHistoryEntry> m_Done = new List<IHistoryEntry>();
        readonly List<IHistoryEntry> m_Undone = new List<IHistoryEntry>();
        APHistoryProxy m_Proxy;
        int m_NextSequence = 1;
        bool m_Busy;

        public int MaxSteps = 40;
        public long MaxBytes = 1536L * 1024 * 1024;

        public event Action Changed;

        public bool CanUndo => m_Done.Count > 0;
        public bool CanRedo => m_Undone.Count > 0;
        public string UndoName => CanUndo ? m_Done[m_Done.Count - 1].Name : null;
        public IHistoryEntry Top => CanUndo && m_Undone.Count == 0 ? m_Done[m_Done.Count - 1] : null;
        public string RedoName => CanRedo ? m_Undone[m_Undone.Count - 1].Name : null;

        public long Bytes
        {
            get
            {
                long b = 0;
                foreach (var e in m_Done) b += e.Bytes;
                foreach (var e in m_Undone) b += e.Bytes;
                return b;
            }
        }

        public History()
        {
            m_Proxy = ScriptableObject.CreateInstance<APHistoryProxy>();
            m_Proxy.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontUnloadUnusedAsset | HideFlags.DontSaveInBuild;
            m_Proxy.name = "Avi Creator Essentials History";
            Undo.undoRedoPerformed += OnUnityUndoRedo;
        }

        public void Push(IHistoryEntry entry)
        {
            if (entry == null) return;
            foreach (var e in m_Undone) e.Dispose(false);
            m_Undone.Clear();

            entry.Sequence = m_NextSequence++;
            m_Done.Add(entry);
            Trim();

            Undo.IncrementCurrentGroup();
            Undo.RecordObject(m_Proxy, "Avi Creator Essentials: " + entry.Name);
            m_Proxy.appliedSequence = entry.Sequence;
            Undo.SetCurrentGroupName("Avi Creator Essentials: " + entry.Name);
            Undo.IncrementCurrentGroup();
            Changed?.Invoke();
        }

        void Trim()
        {
            while (m_Done.Count > 1 && (m_Done.Count > MaxSteps || Bytes > MaxBytes))
            {
                m_Done[0].Dispose(true);
                m_Done.RemoveAt(0);
            }
        }

        /// <summary>Undo through Unity so both histories stay in sync.</summary>
        public void PerformUndo()
        {
            if (!CanUndo) return;
            int before = m_Done.Count;
            Undo.PerformUndo();
            if (m_Done.Count == before && CanUndo)
            {
                // Unity's stack had something else on top; step our own history directly.
                var e = m_Done[m_Done.Count - 1];
                m_Done.RemoveAt(m_Done.Count - 1);
                e.Undo();
                m_Undone.Add(e);
                SyncProxy();
                Changed?.Invoke();
            }
        }

        public void PerformRedo()
        {
            if (!CanRedo) return;
            int before = m_Undone.Count;
            Undo.PerformRedo();
            if (m_Undone.Count == before && CanRedo)
            {
                var e = m_Undone[m_Undone.Count - 1];
                m_Undone.RemoveAt(m_Undone.Count - 1);
                e.Redo();
                m_Done.Add(e);
                SyncProxy();
                Changed?.Invoke();
            }
        }

        void SyncProxy()
        {
            if (m_Proxy == null) return;
            m_Proxy.appliedSequence = m_Done.Count > 0 ? m_Done[m_Done.Count - 1].Sequence : 0;
        }

        void OnUnityUndoRedo()
        {
            if (m_Proxy == null || m_Busy) return;
            m_Busy = true;
            try
            {
                int target = m_Proxy.appliedSequence;
                bool changed = false;
                while (m_Done.Count > 0 && m_Done[m_Done.Count - 1].Sequence > target)
                {
                    var e = m_Done[m_Done.Count - 1];
                    m_Done.RemoveAt(m_Done.Count - 1);
                    e.Undo();
                    m_Undone.Add(e);
                    changed = true;
                }
                while (m_Undone.Count > 0 && m_Undone[m_Undone.Count - 1].Sequence <= target)
                {
                    var e = m_Undone[m_Undone.Count - 1];
                    m_Undone.RemoveAt(m_Undone.Count - 1);
                    e.Redo();
                    m_Done.Add(e);
                    changed = true;
                }
                if (changed) Changed?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                m_Busy = false;
            }
        }

        public void Clear()
        {
            foreach (var e in m_Done) e.Dispose(true);
            foreach (var e in m_Undone) e.Dispose(false);
            m_Done.Clear();
            m_Undone.Clear();
            if (m_Proxy != null) Undo.ClearUndo(m_Proxy);
            Changed?.Invoke();
        }

        public void Dispose()
        {
            Undo.undoRedoPerformed -= OnUnityUndoRedo;
            Clear();
            if (m_Proxy != null)
            {
                Undo.ClearUndo(m_Proxy);
                Object.DestroyImmediate(m_Proxy);
                m_Proxy = null;
            }
        }
    }
}
