# Adding a script to a skill

Read this only when a skill needs to run a script - after the user has confirmed they want one.
A script is for the parts of a task that are deterministic and better done by code than by you:
transforming data, generating a file from a template, running a tool and collecting its output,
anything tedious or easy to get subtly wrong by hand.

## What a skill script is (and what it is NOT)

A skill script is a **bundled asset of the skill**, executed only through the `run_skill_script`
tool. Two rules, both mandatory:

1. **It lives in the skill's own folder**, written with
   `write_skill_file(slug, "scripts/<name>.bat", ...)`. Do **not** create a script in the user's
   workspace, and do not have the skill generate one at runtime.
2. **The `SKILL.md` body runs it with `run_skill_script(slug, "scripts/<name>.bat", [args])`** -
   never `command__run`, and never an instruction to `cd` into a folder and run a loose `.bat`.

If you find yourself writing a skill whose instructions say "run `foo.bat` in the working
directory" or "use command__run to execute the script," stop - that's the wrong shape. The
script belongs *in the skill*, and `run_skill_script` is the only way it runs.

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

## The locations a script sees at runtime

This is the part to get right. A script runs with the user's project as its working directory,
while its own files sit somewhere else entirely - and the two never collide:

- **The working directory (cwd) is the user's workspace** - the project the skill operates on.
  Read inputs and write outputs *here*. This is what the skill is acting on. Never write into the
  skill's own folder.
- **`%~dp0` is the script's own folder** - i.e. `scripts/` (with a trailing backslash), wherever
  the skill was installed. Reach files sitting *next to the script* (the `.py` it wraps, a helper)
  through `%~dp0`. Never by a bare relative name - that would resolve against the workspace, the
  wrong place.
- **`%GXPT_SKILL_DIR%` is the skill's root folder** - the folder that holds `SKILL.md`. Reach
  assets that live at the skill root (a `template.md`, a reference file) through this. (Since a
  script under `scripts/` sits one level below the root, `%~dp0` and `%GXPT_SKILL_DIR%` are *not*
  the same folder - use `%~dp0` for siblings, `%GXPT_SKILL_DIR%` for root-level assets.)

So: **siblings via `%~dp0`, root assets via `%GXPT_SKILL_DIR%`, the user's files via the cwd.** A
script reads its template from `%GXPT_SKILL_DIR%\template.md` and writes the generated result into
the current directory.

The full set of environment variables: `GXPT_SKILL_DIR` (the skill's root folder), `GXPT_WORKDIR`
(the workspace), and `GXPT_SKILL_SLUG` (the slug).

## Never hardcode a path

The skill is installed to a different absolute path on every machine. Locate script-siblings with
`%~dp0`, root assets with `%GXPT_SKILL_DIR%`, and the user's files relative to the cwd. Don't
write `C:\...` anywhere.

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
