// Emits the OpenAPI v1 document to `src/LeaseBook.Web/obj/openapi/` — the input the typed SPA client
// is generated from (ADR-012).
//
// This exists so `npm run api:generate` produces the same client the CI drift gate regenerates. The
// command used to read the document from a running host on :5080, and giving the generator a URL
// makes it bake that origin into `client.gen.ts` as a default `baseUrl` — a value `web/src/api/
// runtime.ts` overrides on every request, and which therefore changed nothing except making the
// committed client differ from CI's, whose error message then advised re-running the command that
// caused it (#369).
//
// Two properties matter and are why this is a script rather than an inline npm command:
//
//   - `LEASEBOOK_OPENAPI_BUILD=1` selects ADR-042's side-effect-free document lifecycle. Without it
//     the document-emitting build boots the host for real, resolves as Production, and dies on a
//     connection string it has no reason to need.
//   - An inline `VAR=1 dotnet build` is shell-specific, and this repo is developed on Windows.
//
// No `shell: true`: `dotnet` is a real executable, Node resolves it on both platforms, and the shell
// option is deprecated when passing an argument list (DEP0190).
//
// Needs the .NET SDK. It does NOT need a running host, a database, or Docker — which is the point:
// the old command needed all three.
//
//   node scripts/emit-openapi.mjs

import { spawnSync } from "node:child_process";
import { existsSync, rmSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
);
const project = path.join("src", "LeaseBook.Web", "LeaseBook.Web.csproj");
const intermediate = path.join(repoRoot, "src", "LeaseBook.Web", "obj");
const document = path.join(intermediate, "openapi", "LeaseBook.Web.json");

// `GenerateOpenApiDocuments` is incremental on this cache, not on the document — so on a warm build
// (the normal case: you just ran the tests) the target is skipped and the document is never written.
// CI never hits this because it builds from a clean checkout. Emission has to be unconditional here,
// or the command works the first time and silently does nothing afterwards.
//
// The document goes too: with only the cache removed, the existence check below proves "a document is
// here", not "this run wrote one", so any future skip would read as success against a stale file.
const cache = path.join(intermediate, "LeaseBook.Web.OpenApiFiles.cache");
rmSync(cache, { force: true });
rmSync(document, { force: true });

const result = spawnSync(
  "dotnet",
  ["build", project, "-c", "Debug", "-p:OpenApiGenerateDocumentsOnBuild=true"],
  {
    cwd: repoRoot,
    env: {
      ...process.env,
      LEASEBOOK_OPENAPI_BUILD: "1",
      // The emitted contract is environment-dependent, which is the single least obvious thing about
      // this script. `dotnet-getdocument` runs `Program.Main`, and `Program.cs` maps `/openapi/v1.json`
      // only in Development — which shifts the endpoint enumeration the document generator walks, so
      // the same 77 paths come out in a different order. Inherit the shell's value and a developer who
      // has run `$env:ASPNETCORE_ENVIRONMENT='Development'` (as the Commands section of AGENTS.md tells
      // them to, and which persists for the whole PowerShell session) emits a differently-ordered
      // document and regenerates a ~240-line diff the drift gate rejects — #369 again, in a third shape.
      //
      // Pinned to Production because that is what CI resolves to, and the gate's document is the
      // canonical one. Both variables are set because `WebApplication.CreateBuilder` reads
      // `DOTNET_ENVIRONMENT` first and `ASPNETCORE_ENVIRONMENT` second; pinning only the winner would
      // leave the loser free to change what "unset" means.
      DOTNET_ENVIRONMENT: "Production",
      ASPNETCORE_ENVIRONMENT: "Production",
    },
    stdio: "inherit",
  },
);

if (result.error) {
  console.error(`Could not run dotnet: ${result.error.message}`);
  console.error("The .NET SDK is required to emit the OpenAPI document.");
  process.exit(1);
}

if (result.status !== 0) {
  process.exit(result.status ?? 1);
}

// The build can succeed without emitting — a silently absent document would otherwise surface as the
// generator failing on a missing input, several steps from the cause.
if (!existsSync(document)) {
  console.error(
    `The build succeeded but wrote no OpenAPI document at ${document}.`,
  );
  console.error(
    "Check that Microsoft.Extensions.ApiDescription.Server is still referenced.",
  );
  process.exit(1);
}

console.log(`OpenAPI document written to ${path.relative(repoRoot, document)}`);
