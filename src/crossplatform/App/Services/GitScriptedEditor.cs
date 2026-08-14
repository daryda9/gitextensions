namespace GitExtensions.Avalonia.Services;

/// <summary>
///  The throw-away <c>/bin/sh</c> script this port hands git when it needs to answer
///  git's editor itself, and the two small rules that come with it.
///
///  <para>Three services drive git through an editor — the rebase session, the merge
///  session and the commit editor — and each of them writes a script, points
///  <c>GIT_EDITOR</c> (or <c>GIT_SEQUENCE_EDITOR</c>) at it, and deletes it afterwards.
///  The mechanism is shared here rather than copied a third time because one of its
///  rules was learnt the hard way and must not be re-learnt: see <see cref="Quote"/>.</para>
///
///  <para><b>The rule its callers obey:</b> nothing is interpolated into a script body
///  that the calling class did not spell out itself. Variable data — a message, a
///  capture path — reaches the script through the child process's environment, where
///  quoting does not exist and a space, a quote or a symlinked path is just a byte.</para>
/// </summary>
internal static class GitScriptedEditor
{
    /// <summary>
    ///  Writes <paramref name="body"/> as an executable <c>/bin/sh</c> script under the
    ///  temp directory and returns its path. The script receives the file git wants
    ///  edited as <c>$1</c>.
    /// </summary>
    internal static string WriteScript(string body)
    {
        string path = Path.Combine(Path.GetTempPath(), "gex-editor-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }

    /// <summary>
    ///  Wraps a script path so git can invoke it. <b>Not decoration: measured.</b> git does
    ///  not exec <c>GIT_EDITOR</c> / <c>GIT_SEQUENCE_EDITOR</c> directly, it hands the value
    ///  to a shell — so the value is a shell WORD LIST, not a path. With
    ///  <c>TMPDIR=/…/tmp dir 'with' quotes</c> the port's own throw-away script therefore
    ///  never ran at all: git 2.43 reported <i>"/…/dario-job/tmp: not found"</i>, split at
    ///  the first space, and the rebase failed with nothing to show for it. Single-quoting
    ///  (with the <c>'\''</c> dance for an embedded quote) makes the whole path one word,
    ///  and the same measurement then wrote the capture file as intended.
    ///  <para>A temp directory is not something the user chose for us, so this applies to
    ///  every scripted editor the port writes — which is the reason the helper lives in
    ///  one place instead of in each service.</para>
    /// </summary>
    internal static string Quote(string path)
        => "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>Best-effort temp cleanup; a leftover script is harmless.</summary>
    internal static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Deliberately silent: this runs in a finally, and a failure to remove a
            // temp file must never replace the outcome the caller is about to report.
        }
    }
}
