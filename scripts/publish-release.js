const { spawn } = require("child_process");
const fs = require("fs");
const path = require("path");

const packageJsonPath = path.join(__dirname, "..", "package.json");
const releaseNotesPath = path.join(__dirname, "..", "RELEASE_NOTES.md");
const releaseNotesSpecPath = path.join(__dirname, "..", "releaseNodesSpec.md");
// Diffs beyond this are almost certainly generated/vendored noise; the head of the diff plus the file list is enough signal for notes.
const maxDiffChars = 150000;

function run(command, args) {
    return new Promise((resolve, reject) => {
        const child = spawn(command, args, { stdio: ["inherit", "pipe", "pipe"] });
        child.stdout.on("data", (chunk) => process.stdout.write(chunk));
        child.stderr.on("data", (chunk) => process.stderr.write(chunk));
        child.on("error", (err) => reject(err));
        child.on("close", (code) => {
            if (code !== 0) {
                reject(new Error(`${command} ${args.join(" ")} exited with code ${code}`));
                return;
            }
            resolve();
        });
    });
}

function runQuiet(command, args) {
    return new Promise((resolve, reject) => {
        const child = spawn(command, args, { stdio: ["inherit", "pipe", "pipe"] });
        let output = "";
        child.stdout.on("data", (chunk) => {
            output += chunk;
        });
        child.stderr.on("data", (chunk) => {
            output += chunk;
        });
        child.on("error", (err) => reject(err));
        child.on("close", (code) => resolve({ code: code ?? 1, output }));
    });
}

// Runs headless Claude Code (claude -p). The whole prompt goes through stdin so no shell quoting is needed; shell: true is required on Windows because the CLI is a .cmd shim.
function runClaude(input) {
    return new Promise((resolve, reject) => {
        const child = spawn("claude", ["-p"], { shell: true, stdio: ["pipe", "pipe", "pipe"] });
        let output = "";
        let errorOutput = "";
        child.stdout.on("data", (chunk) => {
            output += chunk;
        });
        child.stderr.on("data", (chunk) => {
            errorOutput += chunk;
        });
        child.on("error", (err) => reject(err));
        child.on("close", (code) => {
            if (code !== 0) {
                reject(new Error(`claude -p exited with code ${code}: ${errorOutput}`));
                return;
            }
            resolve(output.trim());
        });
        child.stdin.write(input);
        child.stdin.end();
    });
}

async function getPreviousReleaseTag() {
    const result = await runQuiet("git", ["tag", "--sort=-creatordate"]);
    if (result.code !== 0) {
        return undefined;
    }
    return result.output.split(/\r?\n/).map((t) => t.trim()).find((t) => /^(unsigned-)?v\d/.test(t));
}

// Commit messages here are just "Release vX.Y.Z" — all the real work lands in the release commit itself — so the notes come from the staged diff against the previous release tag. Must run after `git add -A` so new files are included.
async function generateReleaseNotes(tag, previousTag) {
    const log = (await runQuiet("git", ["log", "--oneline", previousTag ? `${previousTag}..HEAD` : "HEAD"])).output;
    const diffArgs = ["diff", "--cached", "--no-color"];
    if (previousTag) {
        diffArgs.push(previousTag);
    }
    let diff = (await runQuiet("git", diffArgs)).output;
    if (diff.length > maxDiffChars) {
        diff = diff.slice(0, maxDiffChars) + "\n... (diff truncated)";
    }
    const template = fs.readFileSync(releaseNotesSpecPath, "utf8");
    const prompt = [
        `You are writing GitHub release notes for TabDesktop ${tag}, a Windows desktop app that shows tab strips above groups of overlapping windows.`,
        `Below are the commits and the full git diff since ${previousTag ?? "the beginning of the repo"}. Write the release notes following this template exactly — same sections, same structure — rendered as GitHub-flavored markdown (section headings, a subheading per feature/fix, bullet lists). Omit a section entirely if there is nothing for it. Describe changes as a user would experience them, most important first; skip internal refactors, build tooling, and version-bump noise unless they matter to users.`,
        "",
        "Template:",
        template,
        "",
        `Output only the markdown body — no title, no preamble, no surrounding code fence, and no AI attribution of any kind.`,
        "",
        "Commits:",
        log,
        "Diff:",
        diff,
    ].join("\n");
    return await runClaude(prompt);
}

async function main() {
    // --unsigned tags unsigned-vX.Y.Z instead of vX.Y.Z, which triggers the signing-free Release (unsigned) workflow rather than the signed one.
    const unsigned = process.argv.includes("--unsigned");
    const packageJson = JSON.parse(fs.readFileSync(packageJsonPath, "utf8"));
    const parts = String(packageJson.version).split(".").map(Number);
    if (parts.length !== 3 || parts.some((n) => !Number.isFinite(n))) {
        console.error(`Unrecognized version "${packageJson.version}" in package.json — expected x.y.z.`);
        process.exit(1);
    }
    parts[2] += 1;
    const version = parts.join(".");
    const tag = unsigned ? `unsigned-v${version}` : `v${version}`;

    const existing = await runQuiet("git", ["rev-parse", "--verify", "--quiet", `refs/tags/${tag}`]);
    if (existing.code === 0) {
        console.error(`Tag ${tag} already exists — set "version" in package.json past it.`);
        process.exit(1);
    }

    console.log(`Building the release exe locally first, so a broken build fails here instead of on GitHub...`);
    await run("node", ["scripts/dotnet.js", "publish", "TabDesktop", "-c", "Release", "-o", "dist"]);

    packageJson.version = version;
    fs.writeFileSync(packageJsonPath, JSON.stringify(packageJson, null, 4) + "\n");

    await run("git", ["add", "-A"]);

    // Notes generation uses the Claude Code subscription (headless claude -p), no API key. A failure falls back to the commit list so the release never blocks on it; the workflow attaches RELEASE_NOTES.md as the release body.
    const previousTag = await getPreviousReleaseTag();
    console.log(`Generating release notes with Claude (diff since ${previousTag ?? "the first commit"})...`);
    let notes;
    try {
        notes = await generateReleaseNotes(tag, previousTag);
    } catch (err) {
        console.error(`Release-notes generation failed, falling back to the commit list:`, err.stack ?? err);
        notes = (await runQuiet("git", ["log", "--oneline", previousTag ? `${previousTag}..HEAD` : "HEAD"])).output.trim() || "See the commit history for changes.";
    }
    console.log(`\n----- Release notes -----\n${notes}\n-------------------------\n`);
    fs.writeFileSync(releaseNotesPath, notes + "\n");
    await run("git", ["add", "RELEASE_NOTES.md"]);

    // Subject stays the bare "Release vX.Y.Z" (tooling and history scans key off it); the notes ride along as the commit body.
    await run("git", ["commit", "-m", `Release ${tag}`, "-m", notes]);
    await run("git", ["push"]);
    await run("git", ["tag", tag]);
    await run("git", ["push", "origin", tag]);
    console.log(`Pushed ${tag} — the ${unsigned ? "Release (unsigned)" : "Release"} workflow is now building the exe and creating the GitHub release: https://github.com/sliftist/tabdesktop/actions`);
}

main().catch((err) => {
    console.error(err.stack ?? err);
    process.exit(1);
});
