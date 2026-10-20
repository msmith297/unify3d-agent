---
name: page-test
title: Page test
description: Developer test page for the PropertyManager options - cascading data choices, fills, units, unit choices, Optional, size, info lines, ShowWhen, Remember, 12 groups. Not a real tool.
kind: tool
icon: gear
agent: true
intelligence: fast
confirm: always
context: selection
category: Developer
max_steps: 50
---

# Page test (developer tool)

This tool only exists to check the PropertyManager page features by hand (see README.md in its folder). It does no work on the model.

When it runs:
1. Reply with the `<tool_inputs>` block exactly as you received it, in a code block, so the user can compare each line with README.md ("Expected payload").
2. Then the macro's output (the `Inputs` accessors), also in a code block.
3. Stop. Do not call any tools and do not change the model.
