//   SparkleShare, a collaboration and sharing tool.
//   Copyright (C) 2010  Hylke Bons <hylkebons@gmail.com>
//
//   This program is free software: you can redistribute it and/or modify
//   it under the terms of the GNU Lesser General Public License as 
//   published by the Free Software Foundation, either version 3 of the 
//   License, or (at your option) any later version.
//
//   This program is distributed in the hope that it will be useful,
//   but WITHOUT ANY WARRANTY; without even the implied warranty of
//   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//   GNU General Public License for more details.
//
//   You should have received a copy of the GNU General Public License
//   along with this program. If not, see <http://www.gnu.org/licenses/>.


using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SparkleLib.Git {

    public class GitRepository : BaseRepository {

        private bool user_is_set;
        private bool is_encrypted;

        private string cached_branch;

        private Regex progress_regex = new Regex (@"([0-9]+)%", RegexOptions.Compiled);
        private Regex speed_regex    = new Regex (@"([0-9\.]+) ([KM])iB/s", RegexOptions.Compiled);
        
        private Regex log_regex = new Regex (@"commit ([a-f0-9]{40})*\n" +
                                             "Author: (.+) <(.+)>\n" +
                                             "Date:   ([0-9]{4})-([0-9]{2})-([0-9]{2}) " +
                                             "([0-9]{2}):([0-9]{2}):([0-9]{2}) (.[0-9]{4})\n" +
                                             "*", RegexOptions.Compiled);

        private Regex merge_regex = new Regex (@"commit ([a-f0-9]{40})\n" +
                                               "Merge: [a-f0-9]{7} [a-f0-9]{7}\n" +
                                               "Author: (.+) <(.+)>\n" +
                                               "Date:   ([0-9]{4})-([0-9]{2})-([0-9]{2}) " +
                                               "([0-9]{2}):([0-9]{2}):([0-9]{2}) (.[0-9]{4})\n" +
                                               "*", RegexOptions.Compiled);

        private string branch {
            get {
                if (!string.IsNullOrEmpty (this.cached_branch)) 
                    return this.cached_branch;

                GitCommand git = new GitCommand (LocalPath, "config core.ignorecase true");
                git.StartAndWaitForExit ();

                while (this.in_merge && HasLocalChanges) {
                    try {
                        ResolveConflict ();
                        
                    } catch (IOException e) {
                        Logger.LogInfo ("Git", Name + " | Failed to resolve conflict, trying again...", e);
                    }
                }

                git = new GitCommand (LocalPath, "config core.ignorecase false");
                git.StartAndWaitForExit ();

                git = new GitCommand (LocalPath, "rev-parse --abbrev-ref HEAD");
                this.cached_branch = git.StartAndReadStandardOutput ();

                return this.cached_branch;
            }
        }


        private bool in_merge {
            get {
                string merge_file_path = new string [] { LocalPath, ".git", "MERGE_HEAD" }.Combine ();
                return File.Exists (merge_file_path);
            }
        }


        public GitRepository (string path, Configuration config) : base (path, config)
        {
            GitCommand git = new GitCommand (LocalPath, "config core.ignorecase false");
            git.StartAndWaitForExit ();

            git = new GitCommand (LocalPath, "config remote.origin.url \"" + RemoteUrl + "\"");
            git.StartAndWaitForExit ();

            string password_file_path = Path.Combine (LocalPath, ".git", "password");

            if (File.Exists (password_file_path))
                this.is_encrypted = true;
        }


        public override List<string> ExcludePaths {
            get {
                List<string> rules = new List<string> ();
                rules.Add (".git");

                return rules;
            }
        }


        public override double Size {
            get {
                string file_path = new string [] { LocalPath, ".git", "info", "size" }.Combine ();

                try {
                    string size = File.ReadAllText (file_path);
                    return double.Parse (size);

                } catch {
                    return 0;
                }
            }
        }


        public override double HistorySize {
            get {
                string file_path = new string [] { LocalPath, ".git", "info", "history_size" }.Combine ();

                try {
                    string size = File.ReadAllText (file_path);
                    return double.Parse (size);

                } catch {
                    return 0;
                }
            }
        }


        private void UpdateSizes ()
        {
            double size         = CalculateSizes (new DirectoryInfo (LocalPath));
            double history_size = CalculateSizes (new DirectoryInfo (Path.Combine (LocalPath, ".git")));

            string size_file_path = new string [] { LocalPath, ".git", "info", "size" }.Combine ();
            string history_size_file_path = new string [] { LocalPath, ".git", "info", "history_size" }.Combine ();

            File.WriteAllText (size_file_path, size.ToString ());
            File.WriteAllText (history_size_file_path, history_size.ToString ());
        }


        public override string CurrentRevision {
            get {
                GitCommand git = new GitCommand (LocalPath, "rev-parse HEAD");
                string output  = git.StartAndReadStandardOutput ();

                if (git.ExitCode == 0)
                    return output;
                else
                    return null;
            }
        }


        public override bool HasRemoteChanges {
            get {
                Logger.LogInfo ("Git", Name + " | Checking for remote changes...");
                string current_revision = CurrentRevision;

                GitCommand git = new GitCommand (LocalPath, "ls-remote --heads --exit-code \"" + RemoteUrl + "\" " + this.branch);
                string output  = git.StartAndReadStandardOutput ();

                if (git.ExitCode != 0)
                    return false;

                string remote_revision = "" + output.Substring (0, 40);

                if (!remote_revision.Equals (current_revision)) {
                    git = new GitCommand (LocalPath, "merge-base " + remote_revision + " master");
                    git.StartAndWaitForExit ();

                    if (git.ExitCode != 0) {
                        Logger.LogInfo ("Git", Name + " | Remote changes found, local: " +
                            current_revision + ", remote: " + remote_revision);

                        Error = ErrorStatus.None;
                        return true;
                    
                    } else {
                        Logger.LogInfo ("Git", Name + " | Remote " + remote_revision + " is already in our history");
                        return false;
                    }
                } 

                Logger.LogInfo ("Git", Name + " | No remote changes, local+remote: " + current_revision);
                return false;
            }
        }


        public override bool SyncUp ()
        {
            if (!Add ()) {
                Error = ErrorStatus.UnreadableFiles;
                return false;
            }

            string message = base.status_message.Replace ("\"", "\\\"");

            if (string.IsNullOrEmpty (message))
                message = FormatCommitMessage ();

            if (message != null)
                Commit (message);

            GitCommand git = new GitCommand (LocalPath, "push --progress \"" + RemoteUrl + "\" " + this.branch);
            git.StartInfo.RedirectStandardError = true;
            git.Start ();

            double percentage = 1.0;

            while (!git.StandardError.EndOfStream) {
                string line   = git.StandardError.ReadLine ();
                Match match   = this.progress_regex.Match (line);
                double speed  = 0.0;
                double number = 0.0;

                if (match.Success) {
                    try {
                        number = double.Parse (match.Groups [1].Value, new CultureInfo ("en-US"));
                    
                    } catch (FormatException) {
                        Logger.LogInfo ("Git", "Error parsing progress: \"" + match.Groups [1] + "\"");
                    }

                    // The pushing progress consists of two stages: the "Compressing
                    // objects" stage which we count as 20% of the total progress, and
                    // the "Writing objects" stage which we count as the last 80%
                    if (line.StartsWith ("Compressing")) {
                        // "Compressing objects" stage
                        number = (number / 100 * 20);

                    } else {
                        // "Writing objects" stage
                        number = (number / 100 * 80 + 20);
                        Match speed_match = this.speed_regex.Match (line);

                        if (speed_match.Success) {
                            try {
                                speed = double.Parse (speed_match.Groups [1].Value, new CultureInfo ("en-US")) * 1024;
                            
                            } catch (FormatException) {
                                Logger.LogInfo ("Git", "Error parsing speed: \"" + speed_match.Groups [1] + "\"");
                            }

                            if (speed_match.Groups [2].Value.Equals ("M"))
                                speed = speed * 1024;
                        }    
                    }

                } else {
                    Logger.LogInfo ("Git", Name + " | " + line);

                    if (FindError (line))
                        return false;
                }

                if (number >= percentage) {
                    percentage = number;
                    base.OnProgressChanged (percentage, speed);
                }
            }

            git.WaitForExit ();
            UpdateSizes ();

            if (git.ExitCode == 0)
                return true;

            Error = ErrorStatus.HostUnreachable;
            return false;
        }


        public override bool SyncDown ()
        {
            GitCommand git = new GitCommand (LocalPath, "fetch --progress \"" + RemoteUrl + "\" " + this.branch);

            git.StartInfo.RedirectStandardError = true;
            git.Start ();

            double percentage = 1.0;

            while (!git.StandardError.EndOfStream) {
                string line   = git.StandardError.ReadLine ();
                Match match   = this.progress_regex.Match (line);
                double speed  = 0.0;
                double number = 0.0;

                if (match.Success) {
                    try {
                        number = double.Parse (match.Groups [1].Value, new CultureInfo ("en-US"));   
                    
                    } catch (FormatException) {
                        Logger.LogInfo ("Git", "Error parsing progress: \"" + match.Groups [1] + "\"");
                    }

                    // The fetching progress consists of two stages: the "Compressing
                    // objects" stage which we count as 20% of the total progress, and
                    // the "Receiving objects" stage which we count as the last 80%
                    if (line.StartsWith ("Compressing")) {
                        // "Compressing objects" stage
                        number = (number / 100 * 20);

                    } else {
                        // "Writing objects" stage
                        number = (number / 100 * 80 + 20);
                        Match speed_match = this.speed_regex.Match (line);
                        
                        if (speed_match.Success) {
                            try {
                                speed = double.Parse (speed_match.Groups [1].Value, new CultureInfo ("en-US")) * 1024;
                                
                            } catch (FormatException) {
                                Logger.LogInfo ("Git", "Error parsing speed: \"" + speed_match.Groups [1] + "\"");
                            }
                            
                            if (speed_match.Groups [2].Value.Equals ("M"))
                                speed = speed * 1024;
                        }
                    }

                } else {
                    Logger.LogInfo ("Git", Name + " | " + line);

                    if (FindError (line))
                        return false;
                }
                

                if (number >= percentage) {
                    percentage = number;
                    base.OnProgressChanged (percentage, speed);
                }
            }

            git.WaitForExit ();
            UpdateSizes ();

            if (git.ExitCode == 0) {
                if (Merge ())
                    return true;
                else
                    return false;

            } else {
                Error = ErrorStatus.HostUnreachable;
                return false;
            }
        }


        public override bool HasLocalChanges {
            get {
                PrepareDirectories (LocalPath);

                GitCommand git = new GitCommand (LocalPath, "status --porcelain");
                string output  = git.StartAndReadStandardOutput ();

                return !string.IsNullOrEmpty (output);
            }
        }


        public override bool HasUnsyncedChanges {
            get {
                string unsynced_file_path =  new string [] { LocalPath, ".git", "has_unsynced_changes" }.Combine ();
                return File.Exists (unsynced_file_path);
            }

            set {
                string unsynced_file_path = new string [] { LocalPath, ".git", "has_unsynced_changes" }.Combine ();

                if (value)
                    File.WriteAllText (unsynced_file_path, "");
                else
                    File.Delete (unsynced_file_path);
            }
        }


        // Stages the made changes
        private bool Add ()
        {
            GitCommand git = new GitCommand (LocalPath, "add --all");
            git.StartAndWaitForExit ();

            return (git.ExitCode == 0);
        }


        // Commits the made changes
        private void Commit (string message)
        {
            GitCommand git;

            if (!this.user_is_set) {
                git = new GitCommand (LocalPath, "config user.name \"" + base.local_config.User.Name + "\"");
                git.StartAndWaitForExit ();

                git = new GitCommand (LocalPath, "config user.email \"" + base.local_config.User.Email + "\"");
                git.StartAndWaitForExit ();

                this.user_is_set = true;
            }

            git = new GitCommand (LocalPath, "commit --all --message=\"" + message + "\" " +
                "--author=\"" + base.local_config.User.Name + " <" + base.local_config.User.Email + ">\"");

            git.StartAndReadStandardOutput ();
        }


        // Merges the fetched changes
        private bool Merge ()
        {
            string message = FormatCommitMessage ();
            
            if (message != null) {
                Add ();
                Commit (message);
            }

            GitCommand git;

            // Stop if we're already in a merge because something went wrong
            if (this.in_merge) {
                 git = new GitCommand (LocalPath, "merge --abort");
                 git.StartAndWaitForExit ();
            
                 return false;
            }

            // Temporarily change the ignorecase setting to true to avoid
            // conflicts in file names due to letter case changes
            git = new GitCommand (LocalPath, "config core.ignorecase true");
            git.StartAndWaitForExit ();

            git = new GitCommand (LocalPath, "merge FETCH_HEAD");
            git.StartInfo.RedirectStandardOutput = false;

            string error_output = git.StartAndReadStandardError ();

            if (git.ExitCode != 0) {
                // Stop when we can't merge due to locked local files
                // error: cannot stat 'filename': Permission denied
                if (error_output.Contains ("error: cannot stat")) {
                    Error = ErrorStatus.UnreadableFiles;
                    Logger.LogInfo ("Git", Name + " | Error status changed to " + Error);

                    git = new GitCommand (LocalPath, "merge --abort");
                    git.StartAndWaitForExit ();

                    git = new GitCommand (LocalPath, "config core.ignorecase false");
                    git.StartAndWaitForExit ();

                    return false;
                
                } else {
                    Logger.LogInfo ("Git", error_output);
                    Logger.LogInfo ("Git", Name + " | Conflict detected, trying to get out...");
                    
                    while (this.in_merge && HasLocalChanges) {
                        try {
                            ResolveConflict ();

                        } catch (Exception e) {
                            Logger.LogInfo ("Git", Name + " | Failed to resolve conflict, trying again...", e);
                        }
                    }

                    Logger.LogInfo ("Git", Name + " | Conflict resolved");
                }
            }

            git = new GitCommand (LocalPath, "config core.ignorecase false");
            git.StartAndWaitForExit ();

            return true;
        }


        private void ResolveConflict ()
        {
            // This is a list of conflict status codes that Git uses, their
            // meaning, and how SparkleShare should handle them.
            //
            // DD    unmerged, both deleted    -> Do nothing
            // AU    unmerged, added by us     -> Use server's, save ours as a timestamped copy
            // UD    unmerged, deleted by them -> Use ours
            // UA    unmerged, added by them   -> Use server's, save ours as a timestamped copy
            // DU    unmerged, deleted by us   -> Use server's
            // AA    unmerged, both added      -> Use server's, save ours as a timestamped copy
            // UU    unmerged, both modified   -> Use server's, save ours as a timestamped copy
            // ??    unmerged, new files       -> Stage the new files

            GitCommand git_status = new GitCommand (LocalPath, "status --porcelain");
            string output         = git_status.StartAndReadStandardOutput ();

            string [] lines = output.Split ("\n".ToCharArray ());
            bool trigger_conflict_event = false;

            foreach (string line in lines) {
                string conflicting_path = line.Substring (3);
                conflicting_path        = EnsureSpecialCharacters (conflicting_path);
                conflicting_path        = conflicting_path.Trim ("\"".ToCharArray ());

                // Remove possible rename indicators
                string [] separators = {" -> \"", " -> "};
                foreach (string separator in separators) {
                    if (conflicting_path.Contains (separator)) {
                        conflicting_path = conflicting_path.Substring (
                            conflicting_path.IndexOf (separator) + separator.Length);
                    }
                }

                Logger.LogInfo ("Git", Name + " | Conflict type: " + line);

                // Ignore conflicts in hidden files and use the local versions
                if (conflicting_path.EndsWith (".sparkleshare") || conflicting_path.EndsWith (".empty")) {
                    Logger.LogInfo ("Git", Name + " | Ignoring conflict in special file: " + conflicting_path);

                    // Recover local version
                    GitCommand git_ours = new GitCommand (LocalPath, "checkout --ours \"" + conflicting_path + "\"");
                    git_ours.StartAndWaitForExit ();

                    string abs_conflicting_path = Path.Combine (LocalPath, conflicting_path);

                    if (File.Exists (abs_conflicting_path))
                        File.SetAttributes (abs_conflicting_path, FileAttributes.Hidden);
            
                    continue;
                }

                Logger.LogInfo ("Git", Name + " | Resolving: " + conflicting_path);

                // Both the local and server version have been modified
                if (line.StartsWith ("UU") || line.StartsWith ("AA") ||
                    line.StartsWith ("AU") || line.StartsWith ("UA")) {

                    // Recover local version
                    GitCommand git_ours = new GitCommand (LocalPath, "checkout --ours \"" + conflicting_path + "\"");
                    git_ours.StartAndWaitForExit ();

                    // Append a timestamp to local version.
                    // Windows doesn't allow colons in the file name, so
                    // we use "h" between the hours and minutes instead.
                    string timestamp  = DateTime.Now.ToString ("MMM d H\\hmm");
                    string our_path = Path.GetFileNameWithoutExtension (conflicting_path) +
                        " (" + base.local_config.User.Name + ", " + timestamp + ")" + Path.GetExtension (conflicting_path);

                    string abs_conflicting_path = Path.Combine (LocalPath, conflicting_path);
                    string abs_our_path         = Path.Combine (LocalPath, our_path);

                    if (File.Exists (abs_conflicting_path) && !File.Exists (abs_our_path))
                        File.Move (abs_conflicting_path, abs_our_path);

                    // Recover server version
                    GitCommand git_theirs = new GitCommand (LocalPath, "checkout --theirs \"" + conflicting_path + "\"");
                    git_theirs.StartAndWaitForExit ();

                    trigger_conflict_event = true;

            
                // The server version has been modified, but the local version was removed
                } else if (line.StartsWith ("DU")) {

                    // The modified local version is already in the checkout, so it just needs to be added.
                    // We need to specifically mention the file, so we can't reuse the Add () method
                    GitCommand git_add = new GitCommand (LocalPath, "add \"" + conflicting_path + "\"");
                    git_add.StartAndWaitForExit ();

                
                // The local version has been modified, but the server version was removed
                } else if (line.StartsWith ("UD")) {
                    
                    // Recover server version
                    GitCommand git_theirs = new GitCommand (LocalPath, "checkout --theirs \"" + conflicting_path + "\"");
                    git_theirs.StartAndWaitForExit ();

            
                // Server and local versions were removed
                } else if (line.StartsWith ("DD")) {
                    Logger.LogInfo ("Git", Name + " | No need to resolve: " + line);

                // New local files
                } else if (line.StartsWith ("??")) {
                    Logger.LogInfo ("Git", Name + " | Found new file, no need to resolve: " + line);
                
                } else {
                    Logger.LogInfo ("Git", Name + " | Don't know what to do with: " + line);
                }
            }

            Add ();

            GitCommand git = new GitCommand (LocalPath, "commit --message \"Conflict resolution by SparkleShare\"");
            git.StartInfo.RedirectStandardOutput = false;
            git.StartAndWaitForExit ();

            if (trigger_conflict_event)
                OnConflictResolved ();
        }


        public override void RestoreFile (string path, string revision, string target_file_path)
        {
            if (path == null)
                throw new ArgumentNullException ("path");

            if (revision == null)
                throw new ArgumentNullException ("revision");

            Logger.LogInfo ("Git", Name + " | Restoring \"" + path + "\" (revision " + revision + ")");

            // git-show doesn't decrypt objects, so we can't use it to retrieve
            // files from the index. This is a suboptimal workaround but it does the job
            if (this.is_encrypted) {
                // Restore the older file...
                GitCommand git = new GitCommand (LocalPath, "checkout " + revision + " \"" + path + "\"");
                git.StartAndWaitForExit ();

                string local_file_path = Path.Combine (LocalPath, path);

                // ...move it...
                try {
                    File.Move (local_file_path, target_file_path);
                
                } catch {
                    Logger.LogInfo ("Git",
                        Name + " | Could not move \"" + local_file_path + "\" to \"" + target_file_path + "\"");
                }

                // ...and restore the most recent revision
                git = new GitCommand (LocalPath, "checkout " + CurrentRevision + " \"" + path + "\"");
                git.StartAndWaitForExit ();
            
            // The correct way
            } else {
                path = path.Replace ("\"", "\\\"");

                GitCommand git = new GitCommand (LocalPath, "show " + revision + ":\"" + path + "\"");
                git.Start ();

                FileStream stream = File.OpenWrite (target_file_path);    
                git.StandardOutput.BaseStream.CopyTo (stream);
                stream.Close ();

                git.WaitForExit ();
            }

            if (target_file_path.StartsWith (LocalPath))
                new Thread (() => OnFileActivity (null)).Start ();
        }


        private bool FindError (string line)
        {
            Error = ErrorStatus.None;

            if (line.Contains ("WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!") ||
                line.Contains ("WARNING: POSSIBLE DNS SPOOFING DETECTED!")) {
                
                Error = ErrorStatus.HostIdentityChanged;
                
            } else if (line.StartsWith ("Permission denied") ||
                       line.StartsWith ("ssh_exchange_identification: Connection closed by remote host") ||
                       line.StartsWith ("The authenticity of host")) {

                Error = ErrorStatus.AuthenticationFailed;
                
            } else if (line.EndsWith ("does not appear to be a git repository")) {
                Error = ErrorStatus.NotFound;  

            } else if (line.EndsWith ("expected old/new/ref, got 'shallow")) {
                Error = ErrorStatus.IncompatibleClientServer;
                
            } else if (line.StartsWith ("error: Disk space exceeded") ||
                       line.EndsWith ("No space left on device") ||
                       line.EndsWith ("file write error (Disk quota exceeded)")) {

                Error = ErrorStatus.DiskSpaceExceeded;
            }

            if (Error != ErrorStatus.None) {
                Logger.LogInfo ("Git", Name + " | Error status changed to " + Error);
                return true;
            
            } else {
                return false;
            }
        }


        public override List<SparkleChange> UnsyncedChanges {
          get {
              return ParseStatus ();
            }
        }


        public override List<ChangeSet> GetChangeSets ()
        {
            return GetChangeSetsInternal (null);
        }

        public override List<ChangeSet> GetChangeSets (string path)
        {
            return GetChangeSetsInternal (path);
        }   

        private List<ChangeSet> GetChangeSetsInternal (string path)
        {
            List <ChangeSet> change_sets = new List <ChangeSet> ();
            GitCommand git;

            if (path == null) {
                git = new GitCommand (LocalPath, "log --since=1.month --raw --find-renames --date=iso " +
                    "--format=medium --no-color --no-merges");

            } else {
                path = path.Replace ("\\", "/");

                git = new GitCommand (LocalPath, "log --raw --find-renames --date=iso " +
                    "--format=medium --no-color --no-merges -- \"" + path + "\"");
            }

            string output = git.StartAndReadStandardOutput ();

            if (path == null && string.IsNullOrWhiteSpace (output)) {
                git = new GitCommand (LocalPath, "log -n 75 --raw --find-renames --date=iso " +
                    "--format=medium --no-color --no-merges");

                output = git.StartAndReadStandardOutput ();
            }

            string [] lines      = output.Split ("\n".ToCharArray ());
            List<string> entries = new List <string> ();

            // Split up commit entries
            int line_number = 0;
            bool first_pass = true;
            string entry = "", last_entry = "";
            foreach (string line in lines) {
                if (line.StartsWith ("commit") && !first_pass) {
                    entries.Add (entry);
                    entry = "";
                    line_number = 0;

                } else {
                    first_pass = false;
                }

                // Only parse first 250 files to prevent memory issues
                if (line_number < 250) {
                    entry += line + "\n";
                    line_number++;
                }

                last_entry = entry;
            }

            entries.Add (last_entry);

            // Parse commit entries
            foreach (string log_entry in entries) {
                Match match = this.log_regex.Match (log_entry);

                if (!match.Success) {
                    match = this.merge_regex.Match (log_entry);

                    if (!match.Success)
                        continue;
                }

                ChangeSet change_set = new ChangeSet ();

                change_set.Folder    = new SparkleFolder (Name);
                change_set.Revision  = match.Groups [1].Value;
                change_set.User      = new User (match.Groups [2].Value, match.Groups [3].Value);
                change_set.RemoteUrl = RemoteUrl;

                change_set.Timestamp = new DateTime (int.Parse (match.Groups [4].Value),
                    int.Parse (match.Groups [5].Value), int.Parse (match.Groups [6].Value),
                    int.Parse (match.Groups [7].Value), int.Parse (match.Groups [8].Value),
                    int.Parse (match.Groups [9].Value));

                string time_zone     = match.Groups [10].Value;
                int our_offset       = TimeZone.CurrentTimeZone.GetUtcOffset(DateTime.Now).Hours;
                int their_offset     = int.Parse (time_zone.Substring (0, 3));
                change_set.Timestamp = change_set.Timestamp.AddHours (their_offset * -1);
                change_set.Timestamp = change_set.Timestamp.AddHours (our_offset);

                string [] entry_lines = log_entry.Split ("\n".ToCharArray ());

                // Parse file list. Lines containing file changes start with ":"
                foreach (string entry_line in entry_lines) {
                    // Skip lines containing backspace characters
                    if (!entry_line.StartsWith (":") || entry_line.Contains ("\\177"))
                        continue;

                    string file_path = entry_line.Substring (39);

                    if (file_path.Equals (".sparkleshare"))
                        continue;

                    string type_letter    = entry_line [37].ToString ();
                    bool change_is_folder = false;

                    if (file_path.EndsWith (".empty")) { 
                        file_path        = file_path.Substring (0, file_path.Length - ".empty".Length);
                        change_is_folder = true;
                    }

                    try {
                        file_path = EnsureSpecialCharacters (file_path);
                        
                    } catch (Exception e) {
                        Logger.LogInfo ("Local", "Error parsing file name '" + file_path + "'", e);
                        continue;
                    }

                    file_path = file_path.Replace ("\\\"", "\"");

                    SparkleChange change = new SparkleChange () {
                        Path      = file_path,
                        IsFolder  = change_is_folder,
                        Timestamp = change_set.Timestamp,
                        Type      = SparkleChangeType.Added
                    };

                    if (type_letter.Equals ("R")) {
                        int tab_pos         = entry_line.LastIndexOf ("\t");
                        file_path           = entry_line.Substring (42, tab_pos - 42);
                        string to_file_path = entry_line.Substring (tab_pos + 1);

                        try {
                            file_path = EnsureSpecialCharacters (file_path);
                            
                        } catch (Exception e) {
                            Logger.LogInfo ("Local", "Error parsing file name '" + file_path + "'", e);
                            continue;
                        }

                        try {
                            to_file_path = EnsureSpecialCharacters (to_file_path);

                        } catch (Exception e) {
                            Logger.LogInfo ("Local", "Error parsing file name '" + to_file_path + "'", e);
                            continue;
                        }

                        file_path    = file_path.Replace ("\\\"", "\"");
                        to_file_path = to_file_path.Replace ("\\\"", "\"");

                        if (file_path.EndsWith (".empty")) {
                            file_path = file_path.Substring (0, file_path.Length - 6);
                            change_is_folder = true;
                        }

                        if (to_file_path.EndsWith (".empty")) {
                            to_file_path = to_file_path.Substring (0, to_file_path.Length - 6);
                            change_is_folder = true;
                        }
                               
                        change.Path        = file_path;
                        change.MovedToPath = to_file_path;
                        change.Type        = SparkleChangeType.Moved;

                    } else if (type_letter.Equals ("M")) {
                        change.Type = SparkleChangeType.Edited;

                    } else if (type_letter.Equals ("D")) {
                        change.Type = SparkleChangeType.Deleted;
                    }

                    change_set.Changes.Add (change);
                }

                // Group commits per user, per day
                if (change_sets.Count > 0 && path == null) {
                    ChangeSet last_change_set = change_sets [change_sets.Count - 1];

                    if (change_set.Timestamp.Year  == last_change_set.Timestamp.Year &&
                        change_set.Timestamp.Month == last_change_set.Timestamp.Month &&
                        change_set.Timestamp.Day   == last_change_set.Timestamp.Day &&
                        change_set.User.Name.Equals (last_change_set.User.Name)) {

                        last_change_set.Changes.AddRange (change_set.Changes);

                        if (DateTime.Compare (last_change_set.Timestamp, change_set.Timestamp) < 1) {
                            last_change_set.FirstTimestamp = last_change_set.Timestamp;
                            last_change_set.Timestamp      = change_set.Timestamp;
                            last_change_set.Revision       = change_set.Revision;

                        } else {
                            last_change_set.FirstTimestamp = change_set.Timestamp;
                        }

                    } else {
                        change_sets.Add (change_set);
                    }

                } else {
                    // Don't show removals or moves in the revision list of a file
                    if (path != null) {
                        List<SparkleChange> changes_to_skip = new List<SparkleChange> ();

                        foreach (SparkleChange change in change_set.Changes) {
                            if ((change.Type == SparkleChangeType.Deleted || change.Type == SparkleChangeType.Moved)
                                && change.Path.Equals (path)) {

                                changes_to_skip.Add (change);
                            }
                        }

                        foreach (SparkleChange change_to_skip in changes_to_skip)
                            change_set.Changes.Remove (change_to_skip);
                    }
                                    
                    change_sets.Add (change_set);
                }
            }

            return change_sets;
        }


        private string EnsureSpecialCharacters (string path)
        {
            // The path is quoted if it contains special characters
            if (path.StartsWith ("\""))
                path = ResolveSpecialChars (path.Substring (1, path.Length - 2));

            return path;
        }


        private string ResolveSpecialChars (string s)
        {
            StringBuilder builder = new StringBuilder (s.Length);
            List<byte> codes      = new List<byte> ();

            for (int i = 0; i < s.Length; i++) {
                while (s [i] == '\\' &&
                    s.Length - i > 3 &&
                    char.IsNumber (s [i + 1]) &&
                    char.IsNumber (s [i + 2]) &&
                    char.IsNumber (s [i + 3])) {

                    codes.Add (Convert.ToByte (s.Substring (i + 1, 3), 8));
                    i += 4;
                }

                if (codes.Count > 0) {
                    builder.Append (Encoding.UTF8.GetString (codes.ToArray ()));
                    codes.Clear ();
                }

                builder.Append (s [i]);
            }

            return builder.ToString ();
        }


        // Git doesn't track empty directories, so this method
        // fills them all with a hidden empty file.
        //
        // It also prevents git repositories from becoming
        // git submodules by renaming the .git/HEAD file
        private void PrepareDirectories (string path)
        {
            try {
                foreach (string child_path in Directory.GetDirectories (path)) {
                    if (IsSymlink (child_path))
                        continue;

                    if (child_path.EndsWith (".git")) {
                        if (child_path.Equals (Path.Combine (LocalPath, ".git")))
                            continue;
    
                        string HEAD_file_path = Path.Combine (child_path, "HEAD");
    
                        if (File.Exists (HEAD_file_path)) {
                            File.Move (HEAD_file_path, HEAD_file_path + ".backup");
                            Logger.LogInfo ("Git", Name + " | Renamed " + HEAD_file_path);
                        }
    
                        continue;
                    }
    
                    PrepareDirectories (child_path);
                }
    
                if (Directory.GetFiles (path).Length == 0 &&
                    Directory.GetDirectories (path).Length == 0 &&
                    !path.Equals (LocalPath)) {

                    if (!File.Exists (Path.Combine (path, ".empty"))) {
                        try {
                            File.WriteAllText (Path.Combine (path, ".empty"), "I'm a folder!");
                            File.SetAttributes (Path.Combine (path, ".empty"), FileAttributes.Hidden);

                        } catch {
                            Logger.LogInfo ("Git", Name + " | Failed adding empty folder " + path);
                        }
                    }
                }

            } catch (IOException e) {
                Logger.LogInfo ("Git", "Failed preparing directory", e);
            }
        }



        private List<SparkleChange> ParseStatus ()
        {
            List<SparkleChange> changes = new List<SparkleChange> ();

            GitCommand git_status = new GitCommand (LocalPath, "status --porcelain");
            git_status.Start ();
            
            while (!git_status.StandardOutput.EndOfStream) {
                string line = git_status.StandardOutput.ReadLine ();
                line        = line.Trim ();
                
                if (line.EndsWith (".empty") || line.EndsWith (".empty\""))
                    line = line.Replace (".empty", "");

                SparkleChange change;
                
                if (line.StartsWith ("R")) {
                    string path = line.Substring (3, line.IndexOf (" -> ") - 3).Trim ("\" ".ToCharArray ());
                    string moved_to_path = line.Substring (line.IndexOf (" -> ") + 4).Trim ("\" ".ToCharArray ());
                    
                    change = new SparkleChange () {
                        Type = SparkleChangeType.Moved,
                        Path = EnsureSpecialCharacters (path),
                        MovedToPath = EnsureSpecialCharacters (moved_to_path)
                    };
                    
                } else {
                    string path = line.Substring (2).Trim ("\" ".ToCharArray ());
                    change = new SparkleChange () { Path = EnsureSpecialCharacters (path) };
                    change.Type = SparkleChangeType.Added;

                    if (line.StartsWith ("M")) {
                        change.Type = SparkleChangeType.Edited;
                        
                    } else if (line.StartsWith ("D")) {
                        change.Type = SparkleChangeType.Deleted;
                    }
                }

                changes.Add (change);
            }
            
            git_status.StandardOutput.ReadToEnd ();
            git_status.WaitForExit ();

            return changes;
        }


        // Creates a pretty commit message based on what has changed
        private string FormatCommitMessage ()
        {
            string message = "";

            foreach (SparkleChange change in ParseStatus ()) {
                if (change.Type == SparkleChangeType.Moved) {
                    message +=  "< ‘" + EnsureSpecialCharacters (change.Path) + "’\n";
                    message +=  "> ‘" + EnsureSpecialCharacters (change.MovedToPath) + "’\n";

                } else {
                    if (change.Type == SparkleChangeType.Edited) {
                        message += "/";

                    } else if (change.Type == SparkleChangeType.Deleted) {
                        message += "-";

                    } else if (change.Type == SparkleChangeType.Added) {
                        message += "+";
                    }

                    message += " ‘" + change.Path + "’\n";
                }
            }

            if (string.IsNullOrWhiteSpace (message))
                return null;
            else
                return message;
        }


        // Recursively gets a folder's size in bytes
        private long CalculateSizes (DirectoryInfo parent)
        {
            long size = 0;

            try {
                foreach (DirectoryInfo directory in parent.GetDirectories ()) {
                    if (directory.IsSymlink () ||
                        directory.Name.Equals (".git") || 
                        directory.Name.Equals ("rebase-apply")) {

                        continue;
                    }

                    size += CalculateSizes (directory);
                }

            } catch (Exception e) {
                Logger.LogInfo ("Local", "Error calculating directory size", e);
            }

            try {
                foreach (FileInfo file in parent.GetFiles ()) {
                    if (file.IsSymlink ())
                        continue;
                    
                    if (file.Name.Equals (".empty"))
                        File.SetAttributes (file.FullName, FileAttributes.Hidden);
                    else
                        size += file.Length;
                }
                
            } catch (Exception e) {
                Logger.LogInfo ("Local", "Error calculating file size", e);
            }

            return size;
        }

        
        private bool IsSymlink (string file)
        {
            FileAttributes attributes = File.GetAttributes (file);
            return ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint);
        }
    }
}