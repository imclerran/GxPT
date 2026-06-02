using System;
using System.Collections.Generic;
using Mcp35.Core.Protocol;
using Mcp35.Server;
using Mcp35.Server.Process;
using Newtonsoft.Json.Linq;

namespace GitMcpServer
{
    /// <summary>
    /// The Git server's five tools, all run against GXPT_WORKDIR via ProcessRunner driving
    /// GXPT_GIT_PATH. status/diff/log are ReadOnly, commit is Write, push is Destructive — all
    /// host-gated; the server's job is safe construction (servers-spec §4):
    ///   - arguments are built as discrete tokens and quoted once via ArgvQuoter — never
    ///     concatenated raw, so a model value can't be misread as a flag or split;
    ///   - the commit message goes via stdin (git commit -F -), not argv, removing message
    ///     content as an injection surface entirely;
    ///   - diff's path sits after "--" so it can't be parsed as an option.
    /// </summary>
    internal static class GitTools
    {
        private const int DefaultLogMax = 20;
        private const int MaxLogMax = 200;
        private const int GitTimeoutMs = 30000;
        private const int OutputCap = 100000; // chars; trims runaway diffs/logs

        public static void Register(McpServer server, GitConfig config)
        {
            ProcessRunner runner = new ProcessRunner(null);

            server.AddTool("status", "Show the working tree status (porcelain).",
                SchemaBuilder.Object().Build(),
                delegate(ToolCallContext ctx) { return Status(config, runner, ctx); });

            server.AddTool("diff", "Show changes; optionally only staged changes or a single path.",
                SchemaBuilder.Object()
                    .Bool("staged", false, "Show staged (cached) changes instead of the working tree")
                    .Str("path", false, "Limit the diff to this path")
                    .Build(),
                delegate(ToolCallContext ctx) { return Diff(config, runner, ctx); });

            server.AddTool("log", "Show recent commit history.",
                SchemaBuilder.Object().Int("max", false, "Maximum commits to show (default 20)").Build(),
                delegate(ToolCallContext ctx) { return Log(config, runner, ctx); });

            server.AddTool("commit", "Stage and commit changes with a message.",
                SchemaBuilder.Object()
                    .Str("message", true, "The commit message")
                    .Bool("all", false, "Stage all changes (git add -A) before committing")
                    .Build(),
                delegate(ToolCallContext ctx) { return Commit(config, runner, ctx); });

            server.AddTool("push", "Push commits to a remote.",
                SchemaBuilder.Object()
                    .Str("remote", false, "Remote name (e.g. origin)")
                    .Str("branch", false, "Branch name")
                    .Build(),
                delegate(ToolCallContext ctx) { return Push(config, runner, ctx); });
        }

        // ---- read-only ----

        private static CallToolResult Status(GitConfig config, ProcessRunner runner, ToolCallContext ctx)
        {
            List<string> args = new List<string>();
            args.Add("status");
            args.Add("--porcelain=v1");
            args.Add("-b");
            return RunGit(config, runner, args, null);
        }

        private static CallToolResult Diff(GitConfig config, ProcessRunner runner, ToolCallContext ctx)
        {
            List<string> args = new List<string>();
            args.Add("diff");
            if (BoolArg(ctx, "staged", false)) args.Add("--staged");

            string path = ctx.Arguments.Value<string>("path");
            if (!string.IsNullOrEmpty(path))
            {
                args.Add("--");   // everything after this is a pathspec, never a flag
                args.Add(path);
            }
            return RunGit(config, runner, args, null);
        }

        private static CallToolResult Log(GitConfig config, ProcessRunner runner, ToolCallContext ctx)
        {
            int max = IntArg(ctx, "max", DefaultLogMax, 1, MaxLogMax);
            List<string> args = new List<string>();
            args.Add("log");
            args.Add("-n");
            args.Add(max.ToString(System.Globalization.CultureInfo.InvariantCulture));
            args.Add("--pretty=format:%h %an %ad %s");
            args.Add("--date=short");
            return RunGit(config, runner, args, null);
        }

        // ---- write / destructive ----

        private static CallToolResult Commit(GitConfig config, ProcessRunner runner, ToolCallContext ctx)
        {
            string message = ctx.Arguments.Value<string>("message");
            if (string.IsNullOrEmpty(message)) return ToolResults.Error("commit message is required");

            if (BoolArg(ctx, "all", false))
            {
                List<string> add = new List<string>();
                add.Add("add");
                add.Add("-A");
                CallToolResult staged = RunGit(config, runner, add, null);
                if (staged.IsError) return staged;
            }

            // Message via stdin (git commit -F -): never an argv token, so its content can't be
            // misparsed or injected regardless of what characters it contains.
            List<string> args = new List<string>();
            args.Add("commit");
            args.Add("-F");
            args.Add("-");
            return RunGit(config, runner, args, message);
        }

        private static CallToolResult Push(GitConfig config, ProcessRunner runner, ToolCallContext ctx)
        {
            List<string> args = new List<string>();
            args.Add("push");

            string remote = ctx.Arguments.Value<string>("remote");
            string branch = ctx.Arguments.Value<string>("branch");
            if (!string.IsNullOrEmpty(remote))
            {
                args.Add(remote);
                if (!string.IsNullOrEmpty(branch)) args.Add(branch);
            }
            else if (!string.IsNullOrEmpty(branch))
            {
                // A branch with no remote isn't a valid push target on its own.
                return ToolResults.Error("branch specified without a remote");
            }
            return RunGit(config, runner, args, null);
        }

        // ---- shared execution ----

        public static CallToolResult RunGit(GitConfig config, ProcessRunner runner, IList<string> args, string stdin)
        {
            ProcessRequest req = new ProcessRequest();
            req.FileName = config.GitPath;
            req.Arguments = ArgvQuoter.Join(args);   // single Windows-correct quoter (§4)
            req.WorkingDirectory = config.WorkDir;
            req.TimeoutMs = GitTimeoutMs;
            req.StdinText = stdin;

            ProcessResult result;
            try
            {
                result = runner.Run(req);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The git executable couldn't be launched at all (not installed / not on PATH).
                return ToolResults.Error("git was not found. Install Git and ensure it is on your PATH (or set GXPT_GIT_PATH).");
            }
            catch (Exception ex)
            {
                return ToolResults.Error("failed to run git: " + ex.Message);
            }

            if (result.TimedOut)
                return ToolResults.Error("git timed out after " + GitTimeoutMs + " ms");

            if (result.ExitCode != 0)
            {
                string err = Trim(result.StdErr);
                if (string.IsNullOrEmpty(err)) err = Trim(result.StdOut);
                return ToolResults.Error("git exited " + result.ExitCode + ": " + err);
            }

            JObject outp = new JObject();
            outp["exitCode"] = result.ExitCode;
            outp["stdout"] = Cap(result.StdOut);
            string stderr = Trim(result.StdErr);
            if (!string.IsNullOrEmpty(stderr)) outp["stderr"] = Cap(stderr);
            return ToolResults.Json(outp);
        }

        // ---- helpers ----

        private static bool BoolArg(ToolCallContext ctx, string name, bool fallback)
        {
            JToken t = ctx.Arguments[name];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            try { return t.Value<bool>(); }
            catch { return fallback; }
        }

        private static int IntArg(ToolCallContext ctx, string name, int fallback, int min, int max)
        {
            JToken t = ctx.Arguments[name];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            int n;
            try { n = t.Value<int>(); }
            catch { return fallback; }
            if (n < min) return min;
            if (n > max) return max;
            return n;
        }

        private static string Trim(string s)
        {
            return s == null ? null : s.Trim();
        }

        private static string Cap(string s)
        {
            if (s == null) return null;
            if (s.Length <= OutputCap) return s;
            return s.Substring(0, OutputCap) + "\n…[truncated]";
        }
    }
}
