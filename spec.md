






Can we make it so our executable is signed by Microsoft so it doesn't give a warning when they run it? I believe that GitHub Actions allow some kind of workflow to do this. Could you set up what you can and then walk me through what I need to do in the UI to set it up?


Create some kind of installer, putting it beside the exe in the release. So the program will then show up in their start menu, and they'll also be able to uninstall it from add/remove programs.