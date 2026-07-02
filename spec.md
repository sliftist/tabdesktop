




Add an install button. It's going to be a third column. You're going to have to make the text vertical so it will fit. I guess maybe just make the text say save so it's small enough. And what insulation should do is it should make it so it shows up in the start menu and also copies it over to the appropriate place where programs are supposed to live and makes it so if you go to add remove programs to uninstall it, it will uninstall it. We can leave the executable in their downloads folder. That's fine. Oh, and also it'll make it so it automatically starts on startup. But there should also be a toggle in the app so they can toggle that on and off. Which I guess doesn't mean we're going to have to have a third column and a fourth column because we're going to need to add a new button and this other super button that takes up even more vertical room. 

TEST: install button


Get everything signed.


1. Add a LICENSE file first — this is a hard requirement I found checking the repo: SignPath requires an
OSI-approved open-source license, and tabdesktop currently has none. MIT is the standard choice for something like
this — tell me and I'll add it.
2. Apply at signpath.org (https://signpath.org/) — a short form/email with the project info. A human reviews it
(checks it's real, active, not malware), typically days to a couple of weeks.
3. Once approved, they set you up with a SignPath organization; you install their GitHub App on the repo, and get
three identifiers (organization ID, project slug, signing policy slug) plus an API token to put in repo secrets.
4. I rewire the workflow with their flow: build the exe → upload it as a workflow artifact →
signpath/github-action-submit-signing-request@v2 sends it off and returns the signed exe → attach that to the
release. Their GitHub App verifies the artifact really came from a workflow run on GitHub-hosted runners in your
repo — that's their anti-abuse model. It's a ~15-line workflow change; blocked only on the IDs you get at
approval.