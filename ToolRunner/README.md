# toolrunner

Runs named **workflows** (ordered steps) built from small **tools** over one shared **state**.
Everything you edit lives in JSON under `src/ToolRunner/config/`.

    toolrunner debug pluginhost         # run a workflow
    toolrunner restore pluginhost       # undo it: delete generated files, start from the originals
    toolrunner --dry-run debug pluginhost
    toolrunner --host prod debug pluginhost      # same workflow, on a Linux server over ssh
    toolrunner run docker/consumer-logs service=pluginhost
    toolrunner run docker verb=logs service=gateway contains=ERROR expect=absent
    toolrunner doctor | tools | workflows | args | vars

stdout = machine-readable results (JSON); stderr = the human log. Exit: 0 ok, 1 a step failed, 2 usage/config error.

## Open it
Targets **.NET 9** (`net9.0`); the .NET 9 SDK (or newer) is all you need.

Open **ToolRunner.sln** in Visual Studio (File > Open > Project/Solution). Launch profiles are in
`Properties/launchSettings.json`. In VS Code you need the *C# Dev Kit* extension and must open the
folder that contains the .sln.

## Config (src/ToolRunner/config/)
| file | purpose |
|---|---|
| `globals.json` | near-constant values; nest freely (`services.pluginhost.image`); reuse with `${APP_ROOT}` |
| `globals.user.json` | your machine's overrides (gitignored) |
| `globals.<host>.json` | a server: `APP_ROOT` there, plus `ssh.target`, `ssh.port`, `ssh.identity`, `ssh.options` (`;`-separated) |
| `args.json` | `args.<tool>.<intent> = { "verb": ..., ... }` |
| `workflows.json` | `{ "params": [...], "steps": [ { "run": "tool/intent" } ] }` |

Layering, later wins: globals < user < host < `--set KEY=VALUE` < workflow params < tool outputs.
`${NAME}` and `${NAME:-default}` work in every string, including inside `args.json`; names can be computed:
`${services.${service}.image}`.

A step is `{ "run": "tool/intent", "with": { overrides }, "continueOnError": true }`.
All steps are validated before the first one runs.

## Tools and verbs
- **git**: head, status, require-clean, diff (two files, even outside a repo), pull (ff-only, refuses if dirty)
- **file**: derive, delete
- **compose**: up, down, stop, start, restart, ps, config
- **docker**: ps, inspect, logs, history, events, build, run
- **logfile**: search
- **source**: verify (`none` | `label` | `tag`)

Conventions: `into` names the variable a verb writes its main result to. `compose up` records
`STARTED_AT_UTC` (the host's clock); `docker logs/events` and `logfile search` use it as the default `since`.

## The no-edit-in-place rule
`file/derive` writes a modified **copy** (each edit must match exactly the expected number of lines, else
nothing is written). Originals are never touched, so undo = delete the copy / stop passing it
(`compose` takes `files` and `envFile`). `git/diff` shows exactly what differs.

## Gotchas
- Commenting out a service breaks any `depends_on` that points at it; `compose/validate-*` (config -q) catches that before containers are touched.
- `up <service>` starts that service's dependencies unless `--no-deps`, so the tool passes `--no-deps` by default.
- Generated files must not be mistaken for config: they start with a `# GENERATED` line and live in `.toolrunner/` (gitignored).
- `--host` uses the system `ssh` with key auth only (BatchMode); paths on the server must be absolute POSIX paths.

## UI
`src/ToolRunner.Ui` is a local Blazor Server app. Start it with the `ui` launch profile (http://localhost:5180), or
`TOOLRUNNER_CONFIG=<config dir> dotnet run --project src/ToolRunner.Ui`. It runs commands, so keep it on localhost.
It watches `config/` and `history/`, so files edited in Visual Studio show up without a reload.

- **deployment**: the folder that holds your stacks (saved as `DEPLOY_ROOT` in your layer), scanned at start and on demand:
  services and images, plugin assemblies (appsettings vs the .dll files in bind-mounted folders), ports and conflicts,
  bind mounts and whether their sources exist, volumes, environment variables. Everything found becomes a suggestion elsewhere.
- **run**: pick a workflow, a saved command (`tool/intent` from args.json) or any tool + verb. The form is generated from
  the tool's declared arguments; each box offers values that fit its name (`service` -> service names, `image`/`tag` ->
  images from compose and local docker, `envFile`/`files` -> files found, `set` -> env keys, ...), recent values first,
  and every variable as `${NAME}`. Run, dry-run, or **add to chain**; a chain runs top to bottom and can be saved as a
  workflow (its parameters are the `${names}` nothing defines). A tool + verb form can be saved as a command.
- **workflows**: every workflow with its steps' arguments fully resolved as you type parameter values.
- **variables**: click a value to change it. Edits go to `globals.user.json` only; `reset` removes the override. Values
  produced by tools in this session are listed first and win until cleared.
- **logs**: fetch a service's logs, filter by level and text (or `/regex/`), or switch to the digest, which collapses
  lines that differ only in numbers, ids and timestamps into patterns with counts, errors first.
- **history**: every run (UI and command line), and the spool builder for Copilot.
- **file viewer**: every file path in the UI is a link that opens `/view?path=...` in a new window: JSON and XML as
  collapsible trees (or text), other files as numbered lines with find and highlight, logs coloured by level. The
  "raw" link (`/raw?path=...`) hands the file to the browser's own renderer. Files are read through the selected host.
- **light / dark**: the header button switches theme (default: your system setting). Colours keep their meaning in both:
  green ok/added, red failed/missing/removed, amber changed/warning, blue default/dry run/`RAN`, orange produced by a tool.

A **chain** is a workflow that has not been saved yet. Saving it writes a normal workflow to `workflows.json`;
"Edit as chain" loads a workflow back into the chain to change and re-save. A real run logs `RAN {...}` per step: the
arguments it actually ran with, the counterpart of a dry run's `WOULD RUN {...}`.

Saving from the UI rewrites `workflows.json` / `args.json` / `globals.user.json` with standard formatting; comments in
those files are lost (use a `"$comment"` key instead).

## Suppressing a service for local debugging
    toolrunner suppress <service>       # undo: toolrunner unsuppress <service>
Writes a **copy** of the compose file with the service commented out and every other service's `depends_on` on it
relaxed (`file/derive` edits `commentService` + `dropDependsOn`), a copy of the env file with the consumer pointed at
`services.<service>.debugHost/debugPort`, validates both, stops the service and starts the rest from the copies.
`unsuppress` deletes the copies and starts the whole stack from the originals. The originals are never edited.

## Plugins and logs
    toolrunner run plugins verb=verify appsettings=<file> dirs=<bind source dir> [key=Plugins:Assemblies]
    toolrunner digest <service>
`plugins verify` fails if an assembly the appsettings file lists has no .dll under the directories. Without `key` the
names are detected: strings ending `.dll`, and name-like strings under keys mentioning plugin/assembly/module (an
assembly-qualified type yields its assembly). `digest` (or `digest=true` on `docker logs` / `logfile search`) logs the
collapsed patterns instead of the raw text, which also makes a compact section for the spool file.

## Comparing with a reference folder
    toolrunner compare [dir=PATH] [ref=PATH] [format=text]      # defaults: DEPLOY_ROOT (else APP_ROOT) and REFERENCE_ROOT
Says how a deployment folder differs from a known-good copy, by meaning where it can (exit 0 same, 1 different, 2 a folder is missing):
- **env files** by key (order and comments ignored); **JSON settings** by config path, with lists of plain values compared as sets;
- **compose files** by service, image, depends_on, ports, mounts, volumes and environment, each side expanded with its own `.env`
  (so a tag changed in `.env` shows up as a changed image);
- **binaries** by SHA-256; other text by how many lines each side has that the other does not;
- files that differ only in comments, formatting or line endings are labelled as such; files on one side only are listed.
Values of secret-looking names are hidden. The UI's **compare** tab shows the same, filterable by kind, with a per-file raw
line diff; `spool compareRef=PATH` (or the checkbox on the history tab) adds it to the Copilot file.

`sandbox\samples.ps1` generates a sample deployment twice under `sandbox\samples` (`reference` and `current`, the latter with
deliberate differences of every kind) and sets `DEPLOY_ROOT` / `REFERENCE_ROOT` in your layer so the scan and compare have
something realistic to show.

## Is the configuration in effect?
    toolrunner runtime [dir=PATH] [format=text]        # exit 0 everything in effect, 1 something is not
Scans the deployment, runs `docker inspect` on every container, and tallies the two per service: is there a container
and is it running, on the configured image, publishing the configured host ports (and if a port is held by another
container, which one), with the configured bind mounts and volumes, and with the environment the compose file and its
env_files say it should have. Statuses: `ok`, `differs`, `missing` (configured, not in effect), `extra` (in effect, not
configured), `not-running`, `taken`. It shows that something is not in effect, not why; the usual reason is that files
were edited and nothing was restarted. Read-only. In the UI: deployment tab, "check running containers".

## Read-only mode, notes, and what a run shows
- `--read-only` (the green/red switch in the UI header, green by default) refuses any run containing a step that changes
  the deployment, before the first step runs. Each verb declares whether it changes things (commands tab; `toolrunner tools`).
  Dry runs and the tool's own data (history, your variable layer, saved workflows) are never blocked.
- `toolrunner remark <text>` (the note box in the UI header) writes a note into the history stream, between the runs.
  It appears in the history tab and in the spool file as a note written by you.
- A real run logs `RAN {...}` per step (the resolved arguments). Lines a child process wrote to stderr are tagged
  `(stderr)`; that is where docker compose and git report ordinary progress as well as errors, so it is a label, not an alarm.
- Values are shown as they are, passwords included. Only the spool file hides secret-looking values.

## Git
Deployment folders are usually git working trees, and the tool uses that: the deployment tab shows each repository's
branch, commit and uncommitted changes, and `compare ref=git:REF` (or `git:HEAD` in the compare tab) compares the folder
with itself at that commit, so the last release is always available as a reference without keeping a copy.
(Local folders only for now; line-ending-only differences are ignored for a git reference.)

## Publishing plugins
    toolrunner run plugins verb=publish project=<csproj or folder> dir=<plugin folder> appsettings=<file> [key=Plugins:Assemblies]
    toolrunner run plugins verb=loaded service=<service> appsettings=<file> [key=...]
`publish` builds the project, copies its output into the service's plugin folder (files that differ by content; nothing
if the folder is the build output), and makes sure the appsettings file lists the plugin assembly (not its dependencies),
keeping a backup of the file in `.toolrunner/` beside it. `loaded` reads the service's logs since the last start and
reports, per assembly, whether it is mentioned and whether any such line is an error. The UI's plugins tab fills the form
from the scan, remembers it per service, and can run publish, restart (the service and everything that depends on it)
and the log check in one go.

## Debug image in place of a hardened one

    toolrunner debug-image-plan <Dockerfile or project folder>     # reads only: service, base image, context, tag
    toolrunner debug-image <Dockerfile or project folder>          # prepare, build, override, recreate the service
    toolrunner restore-image <service>                             # back to the hardened image

Optional: `--set service=NAME` (else matched by project / assembly name against service and image names) and
`--set baseImage=IMAGE` (else the best candidate).

What `debug-image` does, in order; nothing existing is edited:

1. `debugimage/prepare` writes `Dockerfile.debug` beside the Dockerfile: the runtime stage's base image is swapped
   for an unhardened one, then `USER root`, `ps`/`curl`/`unzip` via apt/apk/dnf, vsdbg in `/vsdbg`,
   `DOTNET_EnableDiagnostics=1`; `Release` becomes `Debug`; later `USER` lines in the runtime stages are commented out.
2. `docker/build-debug-image` builds it as `<service image>-debug`. The stack is still running at this point.
3. `debugimage/use` adds the service to `${COMPOSE_DEBUG_IMAGE}` (a compose override file: image, `pull_policy: never`,
   `user: root`) and writes `attach-<service>.launch.json`.
4. `compose/up-debug-service` runs `up -d --no-deps <service>` with `-f original -f override`: one container is recreated.

Base image candidates come from `docker image ls` and, when `REGISTRY_API` is set, the registry
(Artifactory: `https://HOST/artifactory/api/docker/REPO-KEY`, with `REGISTRY_PREFIX`, `REGISTRY_USER`, `REGISTRY_TOKEN`
in `globals.user.json`). Same kind (aspnet/runtime), same .NET version, not hardened-looking, same distro family first.
List them with `toolrunner run debugimage/candidates like=IMAGE`.

vsdbg is not put in the image. Like Visual Studio's own container debugging, the override file bind-mounts the copy
on this machine at `/remote_debugger` (read-only). It is looked for in `VSDBG_DIR`, then `%USERPROFILE%\vsdbg\vs2017u5`
(where Visual Studio downloads it); for an alpine base its `linux-musl-x64` subfolder is used when present. The run
stops with the download command if it is not there. Alternatives: `vsdbg=copy` bakes the folder into the image,
`vsdbg=download` runs GetVsDbg.sh during the build (needs download.visualstudio.microsoft.com).

Attach from Visual Studio's Command Window with the line the run prints:
`DebugAdapterHost.Launch /LaunchJson:"<attach-SERVICE.launch.json>" /EngineGuid:541B8A8A-6081-4506-9F0A-1CE771DEBC04`.

`docker compose down` finds containers by project name and service name, not by file content, so the override file
does not stop a later `down` from working; the tool always passes the same project directory.

## Documentation

`docs\index.html` is the browsable documentation: overview, ethos, design, quick start, operations, reference,
extending, limits, troubleshooting. Its reference tables (tools and verbs, saved commands, workflows, variables,
build and pipeline settings) are generated from the tool, so regenerate after changing any of them:

    toolrunner docs            # writes docs\index.html
    toolrunner docs out=FILE

The UI serves the same document live at `/docs` (link in the header). The prose is in `config\docs.template.html`.

## Building a deployment for a product

Keycloak is a stack of its own (`keycloak\docker-compose.yml` in the sample template): it owns a named docker network,
the product's stack joins it as `external`, and services reach Keycloak as `http://keycloak:8080`. `build test`
starts the stacks in the order `template.json` lists; a Keycloak that was already running gets the realm through its
admin API instead of the start-up import.

Creates a deployment folder that `docker compose up` can start, from choices that flow downwards:
product.json (entities) > PRODUCT_TITLE > baseline version > templates filled in > Keycloak realm > check > test.

    toolrunner build plan      # entities, template values, the files that would be written, what is still to choose
    toolrunner build write     # write the folder, then check it
    toolrunner build fetch     # download the plugin packages of the baseline version into each service's plugins folder
    toolrunner build check     # variables defined, bind sources exist, plugins present, images named, no port clash
    toolrunner build test      # compose up, verify Keycloak, deploy product.json as the admin, read the portal as the user

The choices are the `BUILD_*` variables (build tab, `--set`, `globals.user.json`).

**Templates** (`BUILD_TEMPLATE_DIR`; the sample in `config\deployment-template` is used when it is empty). Every file
is copied with `{{PLACEHOLDERS}}` filled in; compose's `${VARIABLES}` are left alone. A placeholder nothing supplies
is reported by name. `template.json` is the one file that describes the product-independent structure:
- `services`: which services are Keycloak clients, their URL, their plugins folder and plugin package ids;
- `keycloak`: URL, admin account, where the realm file goes, roles, users (role, and whether they carry the privilege string);
- `test`: which client and user deploy product.json and to which URL; which user opens which portal URL.

**Generated, not templated**: `keycloak/import/realm.json` (unless the template has a file at that path) with clients
(redirect URIs from the service URLs), roles, users, and one user attribute holding the privilege string
(`BUILD_PRIVILEGE_FORMAT` per entity, joined by `BUILD_PRIVILEGE_SEPARATOR`). Keycloak imports it at start-up
(`--import-realm`). Passwords written as `{{SECRET:name}}` are generated once and kept in `.toolrunner\build-secrets.json`
in the deployment folder.

**The test** passes when the portal page, fetched as the end user through the Keycloak sign-in form, contains
`BUILD_EXPECT` (default `view instances of {entity}`) for every entity. A portal that draws its content with
JavaScript needs a browser test instead; the step says so when the page contains scripts.

## Pipelines (Azure DevOps Server + JFrog)

Two YAML pipelines for a product made of three repositories (plugins, config, Selenium tests):

1. **plugins**: build, unit-test, pack as NuGet, push to JFrog.
2. **product**: fetch the plugins of the same version, start the stack, run the Selenium tests, bake config and plugins
   into final images (`pipeline/Dockerfile.<service>`: what was bind-mounted is copied in), push to JFrog.

Both take a run-time parameter `environment` (`dev` or `prod`) that selects the JFrog repositories.

    toolrunner azdo explore            # read Azure DevOps and Artifactory (GETs only); saved to history\exploration.json
    toolrunner azdo assess             # the readiness checklist again, from the saved exploration
    toolrunner azdo yaml out=DIR       # write the YAML and Dockerfiles, one folder per repository
    toolrunner azdo apply confirm=true # register the two pipelines (after the files are committed)

`explore` exits 1 while anything on the checklist is missing. Settings are the `AZDO_*`, `JFROG_*` and `PIPELINE_*`
variables (pipelines tab, `--set`, or `globals.user.json`); `explore` suggests values for the empty ones.

What the exploration reads: REST version, projects, repositories (files, branches, tags; classified as plugins,
config or tests), agent pools with each agent's capabilities, service connections, variable groups, extensions, tasks,
existing pipelines, recent build numbers; in Artifactory the repositories and the versions in the chosen NuGet and
Docker ones.

Assumptions built into the generated YAML, to change there or in `Core/Pipelines.cs`:
- a release is a build of the git tag `<prefix><version>`; any other build is `<last version>-ci.<build id>`; prod
  accepts releases only;
- steps are `bash` (a Windows agent needs Git for Windows on PATH); `docker compose` v2 on the agent;
- JFrog credentials come from a library variable group holding `JFROG_USER` and `JFROG_TOKEN`;
- plugin packages are fetched with `curl` from `api/nuget/<repo>/Download/<id>/<version>` and their `lib/*/*` unpacked
  into the plugins folder;
- the tests read the portal address from the environment variable `PORTAL_URL`.

## Running the UI outside this solution

`start-ui.cmd` starts the UI without Visual Studio (real docker/podman, `src\ToolRunner\config`). From Visual Studio,
the launch profile "ui (real containers)" does the same; the profile "ui" puts the fake docker first on PATH.

## Podman instead of Docker

Set `CONTAINER_ENGINE` to `podman` (variables tab, `globals.user.json`, a host file, or `--set CONTAINER_ENGINE=podman`).
Every `docker ...` and `docker compose ...` the tools run then goes to `podman ...` / `podman compose ...`, locally or
over ssh. Nothing else changes. For things that behave differently under podman see the notes in the hand-over answer:
compose provider and its labels, `host.containers.internal`, rootless ports and mounts.

## The map

The map tab draws each stack: one box per service, entry points on the left, each column holding what the previous
one needs. A solid line is `depends_on`; a dashed line is a setting of one service that names another
(`ORDERS_URL=http://orders-api:8080`) with no `depends_on` behind it. A box shows the version, the image, published
ports, bind mounts, volumes and plugin assemblies (red when a bind source or assembly is missing). Click a box for its
details; hover a line for the setting that makes the connection. "colour by running state" runs the runtime check:
green = as configured, amber = running but not as configured, red = no running container.

The deployment tab's "browse..." lists folders on the selected host, so the deployment folder can be picked.

## Versions

A stack is on one version: what the tags of its own images carry. "The version" a command uses is the `version`
argument, else the variable `VERSION` (pinned on the versions tab or with `--set VERSION=...`), else the detected one.

    toolrunner versions                         # each service's image version; the stack's version; who differs
    toolrunner run version/available repo=PATH image=IMAGE     # git refs and registry tags that are versions
    toolrunner checkout-version <source repo>   # git checkout of the version's ref; refuses a dirty tree
    toolrunner run version/source-matches repo=PATH            # is HEAD the version's commit?

`debug-image` tags its image `<repo>:<version>-debug` and warns (without stopping) when the source is not on the
version's commit.

Three variables hold everything that is specific to how versions are written; change them in `globals.json` or on
the variables tab:

| variable | default | meaning |
|---|---|---|
| `VERSION_PATTERN` | `\d+(\.\d+)+\|latest` | regex: the version inside an image tag (`2.4.1-alpine` gives `2.4.1`) |
| `VERSION_IGNORE` | `(alpine\|debian\|...\|aspnet)[-_.]?v?\d+(\.\d+)*` | parts of a tag that describe the base image; never read as the version |
| `VERSION_GIT_REFS` | `v{version};{version};release/{version};origin/release/{version}` | git refs tried in order for a version |
| `VERSION_LATEST_REFS` | `origin/master;origin/main;master;main` | what `latest` means in git |

Product images are the ones whose tag comes from a `${VARIABLE}`; third-party images (postgres, redis) are ignored.
Registry tags need `REGISTRY_API` (see the debug image section).

Hardened images named `dhi.` are recognised as hardened; the `-dev` variant of the same image and tag is the first
choice of debug base.

## Asking Copilot for a plan

A second spool mode. Instead of a question answered in prose, you state an intent and Copilot must answer with steps
chosen from what the tool can do: STATE, INTENT, OPTIONS, PLAN, ACTION, OUTCOME.

    toolrunner spool intent="find out why gateway cannot reach pluginhost" scanDir=${DEPLOY_ROOT}
    toolrunner plan reply.txt              # check Copilot's reply: every name and argument against what exists
    toolrunner plan reply.txt run=true     # run it (honours --dry-run and --read-only)
    toolrunner spool intent="..."          # again: RECENT RUNS now holds the outcome for Copilot to assess

- The spool gains an OPTIONS section: every workflow, saved command and tool verb (plugin commands included), each
  marked `[reads]` or `[CHANGES]`, with the values it needs.
- `spool.plan.header.txt` and `spool.plan.footer.txt` in the config folder hold the instructions and the answer format;
  edit them freely, but keep the JSON shape of the PLAN section, which `plan` reads.
- In the UI: history tab, "ask for a plan"; paste the reply under "Copilot's plan", check it, load it onto the run tab
  as a chain, dry-run, run. Loading writes a note with the intent and the expected outcome into the history.
- A plan with any problem (unknown name, unknown or missing argument, value nobody supplies) is shown, never run.

## Shell commands as steps

    toolrunner run shell verb=query "command=git -C ${APP_ROOT} describe --tags" into=DESCRIBED
    toolrunner run shell verb=query command=my-version.cmd capture=lines prefix=APP_

- `${NAME}` in the command is replaced with toolrunner's variable before it runs. Write the shell's own variables
  as `%NAME%`, `$env:NAME` or `$NAME`.
- Output comes back as variables: all of stdout in `into` (default `SHELL_OUT`), the exit code in `SHELL_EXIT`, and
  optionally `capture=lines` (every `NAME=value` line), `capture=json` (each property of a JSON object) or `regex`
  (each named group).
- `shell`: `cmd` (default here), `powershell`, `sh` (default over ssh), `bash`. Several lines are fine.
- `run` counts as changing the deployment; `query` is your statement that the command only reads.

## Your own .NET commands (the plugins folder)

Drop assemblies into `plugins\` beside `ToolRunner.sln` (or the folder `%TOOLRUNNER_PLUGINS%` names). Every public
class with a parameterless constructor that implements an interface named `IDotnetTool` is a command:

    [Args(new[] { "InputFilePath", "OutputFilePath" })]
    public sealed class UpperCaseFile : IDotnetTool
    {
        public async Task<Dictionary<string, string>> ExecAsync(string[] args) { ...; return new() { ["OUTPUT_FILE"] = args[1] }; }
    }

- The class name is the verb of the tool `dotnet`; the `[Args]` names are its arguments, passed in that order.
- Every returned pair becomes a variable for the following steps. Throwing fails the step with the message.
- Optional `[Description("...")]` and `[ReadOnly]`; without `[ReadOnly]` the command counts as changing the deployment.
- The types are matched by name: copy `src\SamplePlugin\Contract.cs` into your assembly. Nothing references toolrunner.
- The folder is re-read when a file in it changes; the files are never locked. `src\SamplePlugin\SamplePlugin.csproj`
  has the post-build copy step.

    toolrunner run dotnet verb=list
    toolrunner run dotnet verb=ExtractText Text=orders:2.4.1 Pattern=\d+\.\d+\.\d+

In a workflow: `{ "run": "dotnet", "with": { "verb": "UpperCaseFile", "InputFilePath": "...", "OutputFilePath": "..." } }`,
or a named set under `"dotnet"` in `args.json`. After adding commands run `toolrunner schema` (a test checks it).

## Tests
`tests/ToolRunner.Tests` (xUnit; run from Test Explorer, or `dotnet test`). Right-click a test > Debug to step through
the real engine with breakpoints.
- **Sandbox tests** build a throwaway world in a temp directory (two git repos, compose and env files, a copy of the
  shipped config, the fake docker first on PATH) and run the shipped workflows in-process: `debug`, `restore`, `isolate`,
  and the ways they must refuse (dirty tree, unstamped image, moved source). They never touch your real config.
- **Integrity tests** fail when things drift apart: a tool's declared `Spec` vs the arguments its code reads, shipped
  `args.json`/`workflows.json` vs the engine, and the committed `config/schemas/*` vs what `toolrunner schema` generates now
  (fix: run `toolrunner schema`).
- Unit tests cover `file/derive`, variable expansion and layering, the env-var scanner, the spool file and the history journal.

## Scanning a directory
    toolrunner scan dir=C:/src/myapp [depth=6] [format=text]       # add --host NAME to scan a server over ssh
Recurses `dir` for compose files (`docker-compose*.yml/yaml`, `compose.y*ml`, `compose.*.y*ml`) and env files, skipping
`.git`, `node_modules`, `bin`, `obj`, `.toolrunner` and similar. Compose files in one directory form a **stack** (an
override file joins its base). Per stack it reports:
- **variables**, as `scan` does, using the `.env` beside the compose file;
- **ports**: host ip, published port, container port, protocol, `expose`d ports, and **conflicts** (the same host port
  published by more than one service, across all stacks found);
- **volumes**: named, anonymous and tmpfs, flagging named volumes the file never declares and external ones;
- **bind mounts**: the resolved absolute host path, read-only or not, whether the source exists on the host, and bind
  sources shared by several services.
`${VAR}` / `${VAR:-default}` in ports and paths are expanded from the stack's `.env`; a reference that cannot be resolved
stays visible (`unresolved`). JSON is the default output; `format=text` prints the summary. The UI's Scan tab shows the
same results with a directory box.
