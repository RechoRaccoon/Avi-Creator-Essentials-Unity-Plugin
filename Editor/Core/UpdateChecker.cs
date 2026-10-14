using System;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.Networking;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace AviCreatorEssentials
{
    /// <summary>
    /// Checks GitHub once a day for a newer version and knows how this copy was installed, so the window can offer
    /// the right way to update (VRChat Creator Companion, Package Manager git URL, or a manual download).
    /// </summary>
    public static class UpdateChecker
    {
        public const string Version = "0.7.0";
        public const string Repo = "RechoRaccoon/Avi-Creator-Essentials-Unity-Plugin";
        public const string GitUrl = "https://github.com/" + Repo + ".git";
        public const string ListingUrl = "https://rechoraccoon.github.io/Avi-Creator-Essentials-Unity-Plugin/index.json";
        public const string ListingPage = "https://rechoraccoon.github.io/Avi-Creator-Essentials-Unity-Plugin/";
        public const string ReleasesPage = "https://github.com/" + Repo + "/releases";
        const string ManifestUrl = "https://raw.githubusercontent.com/" + Repo + "/main/package.json";
        const string LastCheckKey = "AviCreatorEssentials.Update.LastCheck";
        const string LatestKey = "AviCreatorEssentials.Update.Latest";

        public enum InstallKind
        {
            AssetsFolder,       // copied into Assets by hand
            CreatorCompanion,   // embedded package (how VCC installs)
            GitPackage,         // Package Manager "Add package from git URL"
            Other,
        }

        [Serializable]
        class Manifest
        {
            public string version;
        }

        static UnityWebRequest s_Request;
        public static event Action Changed;
        public static bool Checking => s_Request != null;

        public static string Latest => EditorPrefs.GetString(LatestKey, "");

        public static bool UpdateAvailable
        {
            get
            {
                string latest = Latest;
                if (string.IsNullOrEmpty(latest)) return false;
                return System.Version.TryParse(latest, out var l) && System.Version.TryParse(Version, out var c) && l > c;
            }
        }

        public static InstallKind Kind
        {
            get
            {
                var info = PackageInfo.FindForAssembly(typeof(UpdateChecker).Assembly);
                if (info == null) return InstallKind.AssetsFolder;
                switch (info.source)
                {
                    case PackageSource.Git: return InstallKind.GitPackage;
                    case PackageSource.Embedded: return InstallKind.CreatorCompanion;
                    default: return InstallKind.Other;
                }
            }
        }

        /// <summary>Checks GitHub at most once a day unless forced.</summary>
        public static void Check(bool force)
        {
            if (s_Request != null) return;
            string last = EditorPrefs.GetString(LastCheckKey, "");
            if (!force && DateTime.TryParse(last, out var when) && (DateTime.UtcNow - when).TotalHours < 24) return;
            s_Request = UnityWebRequest.Get(ManifestUrl);
            s_Request.timeout = 15;
            s_Request.SendWebRequest();
            EditorApplication.update += Poll;
        }

        static void Poll()
        {
            if (s_Request == null || !s_Request.isDone) return;
            EditorApplication.update -= Poll;
            try
            {
                if (s_Request.result == UnityWebRequest.Result.Success)
                {
                    var m = JsonUtility.FromJson<Manifest>(s_Request.downloadHandler.text);
                    if (m != null && !string.IsNullOrEmpty(m.version)) EditorPrefs.SetString(LatestKey, m.version);
                    EditorPrefs.SetString(LastCheckKey, DateTime.UtcNow.ToString("o"));
                }
            }
            catch (Exception)
            {
                // Offline or GitHub unavailable: try again next time.
            }
            finally
            {
                s_Request.Dispose();
                s_Request = null;
                Changed?.Invoke();
            }
        }

        /// <summary>Does the update in the way that fits how the tool was installed.</summary>
        public static void Update()
        {
            switch (Kind)
            {
                case InstallKind.GitPackage:
                    Client.Add(GitUrl);
                    EditorUtility.DisplayDialog("Avi Creator Essentials", "Updating to " + Latest + ". Unity will reload scripts when the download finishes.", "OK");
                    break;
                case InstallKind.CreatorCompanion:
                    EditorUtility.DisplayDialog("Avi Creator Essentials",
                        "Version " + Latest + " is available.\n\nOpen the VRChat Creator Companion, go to this project's Manage Project page " +
                        "and press the update arrow next to Avi Creator Essentials.", "OK");
                    break;
                default:
                {
                    int choice = EditorUtility.DisplayDialogComplex("Avi Creator Essentials",
                        "Version " + Latest + " is available.\n\nFor one-click updates from now on, add Avi Creator Essentials to the " +
                        "VRChat Creator Companion and install it from there (it removes this manually copied folder for you).\n\n" +
                        "Or download the new version and replace the folder by hand.",
                        "Add to Creator Companion", "Cancel", "Download");
                    if (choice == 0) Application.OpenURL(ListingPage);
                    else if (choice == 2) Application.OpenURL(ReleasesPage);
                    break;
                }
            }
        }

        public static string InstallDescription
        {
            get
            {
                switch (Kind)
                {
                    case InstallKind.CreatorCompanion: return "Installed with the VRChat Creator Companion";
                    case InstallKind.GitPackage: return "Installed from a Git URL";
                    case InstallKind.AssetsFolder: return "Copied into Assets";
                    default: return "Installed as a package";
                }
            }
        }
    }
}
