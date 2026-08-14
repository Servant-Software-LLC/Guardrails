# Seeds the recipient the greeting is addressed to. The plan dictates the exact content,
# so this is a script action — there is nothing for a model to decide.
New-Item -ItemType Directory -Force -Path 'out' | Out-Null
Set-Content -Path 'out/recipient.txt' -Value 'World' -NoNewline
exit 0
