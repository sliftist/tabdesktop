const { spawn } = require("child_process");

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

async function main() {
    const message = process.argv.slice(2).join(" ").trim();
    if (!message) {
        console.error(`Usage: yarn commit-push "commit message"`);
        process.exit(1);
    }
    await run("git", ["add", "-A"]);
    await run("git", ["commit", "-m", message]);
    await run("git", ["push"]);
}

main().catch((err) => {
    console.error(err.stack ?? err);
    process.exit(1);
});
