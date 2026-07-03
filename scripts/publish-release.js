const { spawn } = require("child_process");
const fs = require("fs");
const path = require("path");

const packageJsonPath = path.join(__dirname, "..", "package.json");

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

async function main() {
    const packageJson = JSON.parse(fs.readFileSync(packageJsonPath, "utf8"));
    const parts = String(packageJson.version).split(".").map(Number);
    if (parts.length !== 3 || parts.some((n) => !Number.isFinite(n))) {
        console.error(`Unrecognized version "${packageJson.version}" in package.json — expected x.y.z.`);
        process.exit(1);
    }
    parts[2] += 1;
    const version = parts.join(".");
    const tag = `v${version}`;

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
    await run("git", ["commit", "-m", `Release ${tag}`]);
    await run("git", ["push"]);
    await run("git", ["tag", tag]);
    await run("git", ["push", "origin", tag]);
    console.log(`Pushed ${tag} — the Release workflow is now building the exe and creating the GitHub release: https://github.com/sliftist/tabdesktop/actions`);
}

main().catch((err) => {
    console.error(err.stack ?? err);
    process.exit(1);
});
