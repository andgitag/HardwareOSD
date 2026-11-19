using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace HardwareOSD.Core.Security;

/// <summary>How the current process can start ETW sessions.</summary>
public enum EtwAccessKind
{
    /// <summary>Token contains Performance Log Users or the process is elevated.</summary>
    Active,

    /// <summary>The local group contains the user, but the current token predates the change.</summary>
    GrantedNeedsRelogon,

    /// <summary>Neither administrator rights nor Performance Log Users membership is active.</summary>
    NotGranted,

    /// <summary>The local group could not be queried.</summary>
    Unavailable,
}

/// <summary>Snapshot of the current account's ETW capability.</summary>
public readonly record struct EtwAccessState(
    bool IsElevated,
    bool TokenHasGroup,
    bool GroupHasUser,
    string GroupName,
    string UserName,
    string? Error)
{
    public bool HasAccess => IsElevated || TokenHasGroup;

    public bool NeedsGrant => !HasAccess && !GroupHasUser;

    public bool NeedsRelogon => !HasAccess && GroupHasUser;

    public EtwAccessKind Kind => HasAccess
        ? EtwAccessKind.Active
        : GroupHasUser ? EtwAccessKind.GrantedNeedsRelogon
        : Error is null ? EtwAccessKind.NotGranted
        : EtwAccessKind.Unavailable;
}

/// <summary>Result of the elevated one-time grant helper.</summary>
public readonly record struct EtwGrantResult(bool Success, bool Cancelled, string Message);

/// <summary>
/// Minimal, documented local-group access for ETW. FPS capture can run as a standard user when the
/// account is a member of the built-in Performance Log Users group; no administrator token or
/// background service is kept afterwards. The helper never accepts a password and only changes the
/// explicitly requested local group membership.
/// </summary>
public static class PerformanceLogUsers
{
    public const string GroupSid = "S-1-5-32-559";
    private const int ErrorMemberInGroup = 1378;
    private const int ErrorAccessDenied = 5;

    public static string CurrentUserName
    {
        get
        {
            try
            {
                return WindowsIdentity.GetCurrent().Name;
            }
            catch
            {
                return Environment.UserName;
            }
        }
    }

    public static bool IsElevated()
    {
        try
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent())
                .IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static EtwAccessState GetState()
    {
        var userName = CurrentUserName;
        var elevated = IsElevated();
        var groupName = ResolveGroupName() ?? "Performance Log Users";
        var tokenHasGroup = false;
        try
        {
            tokenHasGroup = WindowsIdentity.GetCurrent().Groups?
                .Any(group => string.Equals(group.Value, GroupSid, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch
        {
            // Token inspection is best-effort; the local group query below still works.
        }

        string? error = null;
        var groupHasUser = false;
        try
        {
            groupHasUser = IsMember(groupName, userName);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        return new EtwAccessState(elevated, tokenHasGroup, groupHasUser, groupName, userName, error);
    }

    /// <summary>
    /// Adds <paramref name="userName"/> to Performance Log Users. Must run elevated; the caller is
    /// responsible for the UAC prompt. Returns success even when the user was already a member.
    /// </summary>
    public static EtwGrantResult GrantUser(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return new EtwGrantResult(false, false, "No user name was supplied.");
        }

        if (!IsElevated())
        {
            return new EtwGrantResult(false, false,
                "Administrator approval is required to grant Performance Log Users membership.");
        }

        var groupName = ResolveGroupName();
        if (string.IsNullOrEmpty(groupName))
        {
            return new EtwGrantResult(false, false,
                "The local Performance Log Users group could not be resolved.");
        }
        var members = new LocalGroupMembersInfo3 { DomainAndName = userName };
        var buffer = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Marshal.StructureToPtr(members, buffer, false);
            var result = NetLocalGroupAddMembers(null, groupName, 3, buffer, 1);
            if (result == 0 || result == ErrorMemberInGroup)
            {
                var already = result == ErrorMemberInGroup;
                return new EtwGrantResult(true, false, already
                    ? $"{userName} is already a member of {groupName}. Sign out and back in if the current session still lacks ETW access."
                    : $"{userName} was added to {groupName}. Sign out and back in to activate ETW access.");
            }

            var error = new Win32Exception(result).Message;
            return new EtwGrantResult(false, false,
                $"Could not add {userName} to {groupName} (error {result}: {error}).");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static string? ResolveGroupName()
    {
        try
        {
            var sid = new SecurityIdentifier(GroupSid);
            var account = (NTAccount)sid.Translate(typeof(NTAccount));
            var value = account.Value;
            var slash = value.LastIndexOf('\\');
            return slash >= 0 ? value[(slash + 1)..] : value;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsMember(string? groupName, string userName)
    {
        if (string.IsNullOrEmpty(groupName))
        {
            return false;
        }

        IntPtr buffer = IntPtr.Zero;
        var resume = IntPtr.Zero;
        try
        {
            var result = NetLocalGroupGetMembers(null, groupName, 3, out buffer, -1,
                out var read, out _, ref resume);
            if (result == ErrorAccessDenied)
            {
                throw new UnauthorizedAccessException("Local group membership query was denied.");
            }

            if (result != 0)
            {
                throw new Win32Exception(result);
            }

            for (var i = 0; i < read; i++)
            {
                var entry = Marshal.PtrToStructure<LocalGroupMembersInfo3>(
                    IntPtr.Add(buffer, i * IntPtr.Size));
                if (string.Equals(entry.DomainAndName, userName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // Microsoft accounts may be formatted differently; compare the account leaf too.
                if (entry.DomainAndName is { } name &&
                    name.EndsWith("\\" + userName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                NetApiBufferFree(buffer);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LocalGroupMembersInfo3
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string DomainAndName;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupAddMembers(
        string? serverName, string groupName, int level, IntPtr buffer, int totalEntries);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupGetMembers(
        string? serverName, string groupName, int level, out IntPtr buffer, int preferredMaxLength,
        out int entriesRead, out int totalEntries, ref IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
