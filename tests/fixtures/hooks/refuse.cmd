@echo off
rem A fake hook as a batch file (HookRunnerTests): refuses with exit code 2.
echo refused by a batch hook 1>&2
exit /b 2
