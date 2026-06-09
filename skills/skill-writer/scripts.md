# Adding a script to a skill

Read this only when a skill needs to run a script - after the user has confirmed they want one.
A script is for the parts of a task that are deterministic and better done by code than by you:
transforming data, generating a file from a template, running a tool and collecting its output,
anything tedious or easy to get subtly wrong by hand.

## First, confirm with the user

Before writing any script, tell the user plainly: "I can add a small batch script so this step
runs the same way every time - want me to?" Don't add one silently. If they agree, ask the
clarifying questions you need (inputs, outputs, what counts as success/failure, whether an
interpreter like Python is available on their machine). A script the user didn't ask for is
friction, not help.

## The entry point is a `.bat` (or `.cmd`)

GxPT runs skill scripts through one uniform entry type: a Windows batch file. That is the only
thing the runtime will launch.

- Put the entry under the skill's `scripts/` folder: `scripts/<name>.bat`.
- Write it with `write_skill_file(slug, "scripts/<name>.bat", content)`.
- If the real work is in another language, the `.bat` is a **wrapper** that calls its sibling
  script. The author owns interpreter selection - and graceful failure when the interpreter
  isn't installed.

```bat
@echo off
rem %~dp0 is THIS script's own folder (with trailing backslash), wherever the skill installed.
python "%~dp0gen.py" %*
if errorlevel 9009 echo Python not found on PATH. Install Python or run the steps by hand.
```

You would then also `write_skill_file(slug, "scripts/gen.py", ...)` for the sibling.

## The two locations a script sees at runtime

This is the part to get right. A script runs with two distinct places in play, and they never
collide:

- **The working directory (cwd) is the user's workspace** - the project the skill operates on.
  Read inputs and write outputs *here*. This is what the skill is acting on.
- **`%~dp0` is the skill's own folder** - the read-only home of the script and its bundled
  siblings (templates, the `.py` it wraps). Reach assets that shipped *with the skill* through
  `%~dp0`, never by a relative name (a bare name would resolve against the workspace, the wrong
  place). The same folder is also in `GXPT_SKILL_DIR`.

So: **siblings via `%~dp0`, the user's files via the cwd.** A script reads its template from
`%~dp0template.md` and writes the generated result into the current directory.

Environment variables available: `GXPT_SKILL_DIR` (the skill folder, same as `%~dp0`),
`GXPT_WORKDIR` (the workspace), and `GXPT_SKILL_SLUG`.

## Never hardcode a path

The skill is installed to a different absolute path on every machine. Locate bundled siblings
with `%~dp0`; locate the user's files relative to the cwd. Don't write `C:\...` anywhere.

## How your future self runs it

In the `SKILL.md` body, tell your future self to run the script with the `run_skill_script` tool,
giving the slug, the relative path to the `.bat`, and any literal arguments:

```
run_skill_script(slug, "scripts/gen.bat", ["--since", "v1.2"])
```

Each argument is passed as a literal token - the runtime quotes them, so don't try to build a
shell command string or chain commands. The tool only runs the named skill's declared batch with
the args you pass; it cannot run an arbitrary command.

**Running a script always asks the user to confirm**, every time, showing what's about to run
(the user may choose to remember the approval for that exact script + args). Writing the script
is harmless; *running* it is the gated step - so a freshly written script never runs on its own.
Make the `.bat` well-behaved: print what it did, exit non-zero on failure, and don't touch
anything outside the workspace.

## Keep scripts small and honest

- One script, one job. If a skill needs several distinct deterministic steps, prefer several
  small scripts the workflow calls at the right moments over one that does everything.
- Fail loudly. Exit non-zero and say what went wrong, so your future self can read the error and
  recover instead of assuming success.
- Don't hide important decisions inside the script that the user should see in the workflow.
