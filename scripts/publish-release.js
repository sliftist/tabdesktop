const { spawn } = require("child_process");
const path = require("path");

const packageJson = require(path.join(__dirname, "..", "package.json"));

function run(command, args) {
    return new Promise((resolve, reject) => {
        const child = spawn(command, args, { stdio: ["inherit", "pipe", "pipe"] });
        let output = "";
        child.stdout.on("data", (chunk) => {
            output += chunk;
            process.stdout.write(chunk);
        });
        child.stderr.on("data", (chunk) => {
            output += chunk;
            process.stderr.write(chunk);
        });
        child.on("error", (err) => reject(err));
        child.on("close", (code) => {
            if (code !== 0) {
                reject(new Error(`${command} ${args.join(" ")} exited with code ${code}`));
                return;
            }
            resolve(output);
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
    const tag = `v${packageJson.version}`;

    const status = await runQuiet("git", ["status", "--porcelain"]);
    if (status.output.trim() !== "") {
        console.error(`Working tree has uncommitted changes. Commit (and push) first so the release tag points at what you're shipping.`);
        process.exit(1);
    }

    const existing = await runQuiet("git", ["rev-parse", "--verify", "--quiet", `refs/tags/${tag}`]);
    if (existing.code === 0) {
        console.error(`Tag ${tag} already exists. Bump "version" in package.json first.`);
        process.exit(1);
    }

    await run("git", ["push"]);
    await run("git", ["tag", tag]);
    await run("git", ["push", "origin", tag]);
    console.log(`Pushed ${tag} — the Release workflow is now building the exe and creating the GitHub release: https://github.com/sliftist/tabdesktop/actions`);
}

main().catch((err) => {
    console.error(err.stack ?? err);
    process.exit(1);
});
