using System.Security.Permissions;

// SecurityPermission with SkipVerification lets the Mono JIT skip access-modifier checks,
// so members of the publicized RoR2.dll can be used at runtime (same trick DebugToolkit uses).
#pragma warning disable CS0618 // Type or member is obsolete
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
#pragma warning restore CS0618 // Type or member is obsolete
