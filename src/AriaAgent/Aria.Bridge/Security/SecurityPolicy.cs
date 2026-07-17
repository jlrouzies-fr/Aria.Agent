using System.Runtime.InteropServices;

namespace Aria.Bridge;

public record SecurityPolicy(
    string[]? AllowedPaths    = null,
    string[]? BlockedCommands = null)
{
    // When true, an empty AllowedPaths array means "no paths allowed" rather than "no restriction".
    // This is set only by FromNodeAndRequest when the node has a restriction and the request narrows
    // it to nothing, so the policy still blocks every path.
    private readonly bool _emptyMeansBlockAll;

    private SecurityPolicy(string[]? allowedPaths, string[]? blockedCommands, bool emptyMeansBlockAll)
        : this(allowedPaths, blockedCommands)
    {
        _emptyMeansBlockAll = emptyMeansBlockAll;
    }

    /// <summary>
    /// Builds the effective terminal policy from node-side config and an optional request narrowing.
    /// The node is the authoritative maximum scope. The request may only narrow it:
    /// - allowed paths from the request must be under one of the node allowed paths;
    /// - if the node sets no allowed paths, the effective policy blocks every path;
    /// - blocked commands from the request are added to the node-side and hardcoded blocks.
    /// </summary>
    public static SecurityPolicy FromNodeAndRequest(
        string[]? nodeAllowedPaths,
        string[]? requestAllowedPaths,
        string[]? nodeBlockedCommands = null,
        string[]? requestBlockedCommands = null)
    {
        var (effectiveAllowed, blockAll) = MergeAllowedPaths(nodeAllowedPaths, requestAllowedPaths);
        var effectiveBlocked = (nodeBlockedCommands ?? [])
            .Concat(requestBlockedCommands ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new SecurityPolicy(effectiveAllowed, effectiveBlocked, blockAll);
    }

    private static (string[]? allowed, bool blockAll) MergeAllowedPaths(string[]? node, string[]? request)
    {
        var nodeList = node?.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        var reqList  = request?.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();

        // No node-side restriction: block every path. The node must explicitly declare the
        // directories it is willing to serve; an empty list is not "no restriction" because a
        // compromised server could otherwise send arbitrary allowed paths and bypass the node.
        if (nodeList is not { Length: > 0 })
            return ([], true);

        // Node-side restriction exists: the request may only narrow it.
        if (reqList is not { Length: > 0 })
            return (nodeList, false);

        var narrowed = reqList
            .Where(r => IsUnderAny(nodeList, r))
            .ToArray();

        // If the request narrowed the node restriction to nothing, the effective policy blocks all paths.
        return narrowed.Length > 0 ? (narrowed, false) : ([], true);
    }

    private static bool IsUnderAny(string[] parents, string child)
    {
        try
        {
            var childFull = Path.GetFullPath(child.TrimEnd('/', '\\'));
            return parents.Any(p =>
            {
                var parentFull = Path.GetFullPath(p.TrimEnd('/', '\\'));
                var cmp = IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                return childFull.Equals(parentFull, cmp)
                    || childFull.StartsWith(parentFull + Path.DirectorySeparatorChar, cmp);
            });
        }
        catch { return false; }
    }

    // These patterns are always blocked regardless of user config.
    private static readonly string[] HardBlocked =
    [
        // Unix: wipe entire filesystem or home
        "rm -rf /", "rm -rf /*", "rm -rf ~/", "rm -rf $HOME",
        // Unix: overwrite block devices
        "dd if=/dev/zero of=/dev/sd", "dd if=/dev/zero of=/dev/hd",
        "> /dev/sda", "> /dev/nvme",
        // Unix: reformat disks
        "mkfs",
        // Fork bomb
        ":(){ :|:& };:", ":(){:|:&};:",
        // Unix: exfil via TCP/UDP
        "bash -i >& /dev/tcp", "bash -i >& /dev/udp",
        "nc -e /bin/sh", "nc -e /bin/bash",
        // Privilege escalation shortcuts
        "chmod -R 777 /", "chown -R root /",
        // Access to shadow/passwd
        "/etc/shadow",
        // Windows: wipe C drive
        "format c:", "format c :", "rd /s /q c:\\", "rmdir /s /q c:\\",
        "del /f /s /q c:\\", "del /s /q c:\\windows",
    ];

    private static readonly bool IsWindows =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public void EnforcePath(string path)
    {
        if (AllowedPaths is not { Length: > 0 })
        {
            if (_emptyMeansBlockAll)
                throw new TerminalSecurityException(
                    $"Path '{path}' is outside the allowed directories. The effective allowed-path set is empty.");
            return;
        }

        var full = Path.GetFullPath(path);
        var cmp  = IsWindows
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var allowed = AllowedPaths.Any(p =>
        {
            try
            {
                var norm = Path.GetFullPath(p.TrimEnd('/', '\\'));
                return full.StartsWith(norm + Path.DirectorySeparatorChar, cmp)
                    || full.Equals(norm, cmp);
            }
            catch { return false; }
        });

        if (!allowed)
            throw new TerminalSecurityException(
                $"Path '{path}' is outside the allowed directories. Add it under Terminal › Allowed Paths to grant access.");
    }

    public void EnforceCommand(string command)
    {
        var all = HardBlocked.Concat(BlockedCommands ?? []);
        var hit = all.FirstOrDefault(b =>
            command.Contains(b, StringComparison.OrdinalIgnoreCase));
        if (hit != null)
            throw new TerminalSecurityException(
                $"Command blocked: matches restricted pattern '{hit}'.");
    }
}

public sealed class TerminalSecurityException(string message) : Exception(message);
