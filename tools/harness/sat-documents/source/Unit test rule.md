---
title: Unit Test Rules
subtitle: Design, execution, and reporting of the unit tests used for acceptance
cover: rules
toc: true
---
## 1. Purpose and Scope

This policy applies to the automated tests written for new or modified procedures, functions, and their associated configuration. It defines how the tests are designed, written, run, and reported, so that unit-test results can serve as the primary acceptance criterion set out in Sections 1 and 2 of the Code Review Rules. It uses the same review decisions and evidence workflow as the Code Review Rules; where the two documents differ, the Code Review Rules prevail. Test results are recorded in the unit-test report, Code Review/Unit test Report.xlsx.

## 2. Test Design Rules

- **Basis for expected results:** Each test shall verify behavior stated in a requirement, specification, or agreed business rule. Expected results shall be worked out from that source, not obtained by running the code under test.
- **Expected behavior:** Tests shall cover the normal results of each new or modified procedure or function, including each significant decision branch.
- **Partitions and boundaries:** Inputs that the requirement treats alike shall be grouped into classes, with at least one test for each class. Values on and next to each boundary, such as dates, amounts, limits, and rounding points, shall be tested where applicable. Tests shall also verify the effects of the material hard-coded values and rules identified under Section 3 of the Code Review Rules.
- **Combinations of conditions:** Where a result depends on several conditions, each combination of conditions that the requirement treats differently shall be tested. A decision table may be used to identify the combinations.
- **Invalid input and errors:** Tests shall cover invalid, missing, and malformed input. They shall confirm that each error is reported as specified and that a failed or cancelled operation leaves stored data and output files in the state the requirement specifies.
- **Defect fixes:** A defect fix shall include a test that fails before the fix and passes after it, where feasible.

## 3. Automated Checks and Manual Review

- **Automate deterministic checks:** Rules that a tool can verify consistently should be checked by tools during the build or test run, not by manual review. Manual review should concentrate on the items that tools cannot judge.
- **Test results:** The test framework shall run the tests and report each test as passed, failed, or skipped. A person shall not decide whether an individual test passed.
- **Code coverage:** A coverage tool shall measure which code the tests execute, so that untested behavior can be found. Parts of the software that are not measured shall be stated in the unit-test report. Changed behavior and significant decision branches shall be executed by tests, and any project-defined coverage threshold shall be met. A coverage percentage alone is not evidence of correctness.
- **Architecture rules:** Where the project defines rules for the dependencies between modules, architecture tests shall check them in the same test run.
- **Test effectiveness:** Mutation testing may be used to check whether the tests detect small changes to the code. It should concentrate on high-risk logic and business rules; a complete mutation score is not required.
- **Merge and release:** Where the repository is configured to require checks to pass before code is merged, the required test run shall be one of those checks. A release shall be built from a revision whose required tests have passed.
- **Manual review of tests:** Reviewers shall check that the expected results match the requirements and business rules, that the tests cover the cases required by Section 2, and that the test code is no more complex than necessary.

## 4. Supporting Test Rules

- **Structure:** Each test should follow the arrange, act, and assert pattern and exercise one behavior. Tests should not contain conditional logic or loops that decide the expected result; cases that differ only in their data should be written as parameterized tests.
- **Naming:** Test names should state the subject, the condition, and the expected result, so that a failing test identifies the affected behavior without reading its code.
- **Assertions:** Assertions shall compare specific values, states, or error codes with the expected results. A test that only confirms that execution completes does not verify the behavior, unless completion is the requirement.
- **Isolation:** Each test shall set up the data it needs, or use shared test data that tests do not change, and shall remove anything it creates. Tests shall pass whether they are run alone or with the rest of the suite, in any order.
- **Repeatability:** Inputs that change between runs and affect the expected result, such as the current time, random values, and regional settings, shall be fixed or controlled by the test. Tests shall not depend on network services, or on files other than those kept in the repository and those the test creates.
- **Test data:** Tests shall use synthetic data created for testing. Production data, client data, and data derived from them shall not be used in tests, test data files, or test outputs.
- **Test doubles and integration tests:** Stubs or fakes may replace external dependencies to keep a unit test focused and repeatable. Behavior that depends on a database, the file system, or file formats shall also be verified against the real component by integration tests, which shall create and remove their own temporary resources.
- **Beyond unit tests:** Behavior that unit tests cannot reasonably exercise, such as code that drives the user interface or the hosting application, shall be verified by integration, system, or user-interface tests, as required by Section 4 of the Code Review Rules.
- **Maintenance:** Tests shall be kept with the source code under version control and updated with the behavior they verify. When an existing test is changed, the change description shall state the reason. Tests shall not be removed, weakened, or altered solely to obtain a passing result.

## 5. Test Decision and Exceptions

- **Pass:** All required tests pass on the revision submitted for approval, no required test is skipped without approval, and the unit-test report is complete.
- **Changes required:** Any required test fails; a required test is missing, skipped without approval, or fails intermittently; or the unit-test report is incomplete. The change shall not be merged or released until these items are resolved. A pass obtained only by rerunning a failed test does not count as a pass.
- **Pass with approved exception:** Where a required test cannot reasonably be performed, the exception shall be approved and recorded as set out in Section 5 of the Code Review Rules. Missing or impractical unit tests shall not be treated as an automatic pass.
- **Re-test:** After any code change, the affected tests shall be rerun and the unit-test report updated. Final approval shall apply to the revision supported by the recorded results.

## 6. Required Evidence and Test Workflow

1. **Author writes tests:** Write or update the tests according to Sections 2 and 4, together with the code change.
2. **Author runs tests:** Run the tests affected by the change, and run the complete automated test suite before release, on the revision to be submitted. Tests excluded by design, such as long-running performance tests, shall be identified by a documented filter.
3. **Author reports:** Record the software name and version, execution date, test environment, test data, commands, acceptance criterion, the number of tests passed, failed, and skipped for each test project, and the overall result in the unit-test report. Also record code coverage, the parts not measured, and the areas not covered. For the core business logic, list the test items with their input conditions and expected results.
4. **Reviewer verifies and records:** Check the tests and the unit-test report against this policy as part of the review under Section 6 of the Code Review Rules, and record the result in the review record.
