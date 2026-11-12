---
title: Code Review Rules
english: Unit-test-based acceptance with manual review of hard-coded content
cover: plain
header: Code Review Rules
toc: true
---
## 1. Purpose and Scope

This policy applies to new or modified procedures, functions, and their associated configuration. Unit-test results shall be the primary acceptance criterion for most procedures and functions. Manual review shall supplement automated testing, particularly for hard-coded values, business rules, and behavior that tests do not adequately verify. Passing unit tests alone does not constitute code review approval.

## 2. Unit-Test Acceptance Rules

- **Required tests:** Each new or modified procedure or function shall have relevant unit tests, unless an approved exception applies. Tests shall cover expected behavior, applicable boundary conditions, invalid inputs, and error handling.
- **Pass criterion:** All required tests for the change and the affected regression suite shall pass against the exact code revision submitted for approval. Any unresolved failure blocks approval.
- **Test quality:** Assertions shall verify meaningful expected results, not merely that execution completes. Expected results shall be derived from requirements or agreed business rules, rather than copied from the implementation.
- **Repeatability:** Required tests shall produce repeatable results. External dependencies shall be controlled where appropriate. Skipped, disabled, or unstable required tests shall not count as a pass.
- **Coverage:** Reviewers shall confirm that changed behavior and significant decision branches are exercised. Any project-defined coverage threshold shall also be met; a coverage percentage alone is not sufficient evidence of correctness.
- **Regression protection:** Defect fixes shall include a test that reproduces the defect where feasible. Tests shall not be removed, weakened, or altered solely to obtain a passing result.

## 3. Manual Review of Hard-Coded Content

- **Identify hard-coded content:** Inspect changed code for literal values and embedded rules, including thresholds, dates, rates, identifiers, file paths, service endpoints, environment settings, and fixed decision logic.
- **Validate the source:** Confirm that each material value or rule matches an approved requirement, specification, or configuration source. Record its purpose and any relevant units, effective dates, or assumptions.
- **Assess maintainability:** Values that vary by environment, deployment, or business policy should be moved to an appropriate configuration mechanism. Stable values may remain as named constants where their meaning and justification are clear. Not every literal requires external configuration.
- **Protect sensitive information:** Credentials, passwords, tokens, and other secrets shall not be embedded in source code. Any such finding blocks approval and shall be handled through the applicable security process.
- **Verify behavior:** Check applicable boundaries, precision, rounding, defaults, and fallback behavior. Tests shall verify the effects of hard-coded rules where feasible; manual review shall verify that the rules themselves are justified.
- **Record the conclusion:** Each material finding shall be marked acceptable with justification, corrected, or covered by an approved exception. Unresolved findings block approval.

## 4. Supporting Review Rules

- **Requirements and scope:** Confirm that the change implements the intended requirement without unrelated changes or unintended side effects.
- **Readability and consistency:** Check clear naming, understandable control flow, appropriate comments, avoidance of unnecessary duplication, and compliance with the project's coding standards.
- **Error handling and security:** Inspect input validation, exception handling, resource cleanup, sensitive-data handling, and relevant access checks.
- **Beyond unit tests:** Require integration, system, or other appropriate verification when behavior depends on databases, external services, deployment configuration, or interactions not covered by unit tests. Unit-test success shall not replace these checks.
- **Independent review:** A reviewer other than the author shall review the change and its evidence. Higher-risk changes shall receive any additional review required by the project.

## 5. Review Decision and Exceptions

- **Pass:** All required tests pass, manual review is complete, hard-coded content is accepted or corrected, other required checks are complete, and no blocking findings remain.
- **Changes required:** Any required test fails; required evidence is missing; a required test is skipped without approval; or manual review identifies an unresolved issue. The change shall not be merged or released until the blocking items are resolved.
- **Pass with approved exception:** A designated project approver may authorize a documented exception where a required check cannot reasonably be performed. The record shall identify the affected code, rationale, risk, alternative verification, approver, and any follow-up owner and deadline. Missing or impractical unit tests shall not be treated as an automatic pass.
- **Re-review:** After code changes, rerun affected tests and review the revised code. Final approval shall apply to the revision supported by the recorded evidence.

## 6. Required Evidence and Review Workflow

- **Author submits:** Provide the change description, requirement reference, affected procedures or functions, code revision, unit-test report, and a list of material hard-coded values or rules with their justification.
- **Reviewer verifies:** Check test results and adequacy, manually inspect the changed code and hard-coded content, and confirm any additional required verification.
- **Author resolves:** Correct findings, update tests as needed, rerun required checks, and provide revised evidence.
- **Reviewer records:** Document the final decision, reviewer, review date, approved code revision, outstanding follow-up actions, and any approved exceptions in the review record.
