using System;

namespace Editor.Core
{
    /// <summary>
    /// Single source of truth for the running engine version. Bump this together with
    /// <c>Installer/VortexEngine.iss</c> (<c>#define MyAppVersion</c>) on every release — the auto-updater
    /// compares this to the latest GitHub release tag.
    /// </summary>
    public static class EngineInfo
    {
        /// <summary>Current engine version (semver: major.minor.patch).</summary>
        public static readonly Version Version = new Version(3, 4, 3);

        /// <summary>Version as a bare string, no leading 'v' (e.g. "2.3.0").</summary>
        public static string VersionString => Version.ToString(3);

        /// <summary>The public GitHub repo the updater checks for releases.</summary>
        public const string RepoOwner = "shadow-kernel";
        public const string RepoName = "Vortex-Engine";

        /// <summary>
        /// Project format/schema revision. Bump this ONLY when a change makes older projects require migration.
        /// A project whose stored formatVersion is lower than this needs migrating before it can open. Missing /
        /// absent is treated as 1 (legacy projects are considered current, not force-migrated).
        /// History: 2 = animation era (Assets/Animations + refreshed scripting stub, MigrateV1ToV2).
        /// </summary>
        public const int CurrentProjectFormatVersion = 2;
    }
}
