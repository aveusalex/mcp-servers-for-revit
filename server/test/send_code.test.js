import { test } from "node:test";
import assert from "node:assert/strict";

const { formatCodeFailure } = await import("../build/tools/send_code_to_revit.js");

test("formatCodeFailure keeps errorMessage first and appends hints and structured details", () => {
  const text = formatCodeFailure({
    success: false,
    errorMessage: "Compilation failed:\nLine 2: CS0103 The name 'doc' does not exist in the current context",
    errorType: "compile",
    line: 2,
    errors: [{ code: "CS0103", line: 2, column: 5, message: "The name 'doc' does not exist in the current context" }],
    hints: ["The target Document is available as 'document'."],
    transactionMode: "rollback",
    rolledBack: false,
  });

  assert.ok(text.startsWith("Code execution failed: Compilation failed:"));
  assert.match(text, /Hints:\n- The target Document is available as 'document'\./);
  const details = JSON.parse(text.slice(text.indexOf("{")));
  assert.equal(details.errorType, "compile");
  assert.equal(details.line, 2);
  assert.equal(details.errors[0].code, "CS0103");
  assert.equal(details.transactionMode, "rollback");
});

test("formatCodeFailure tolerates the old add-in response (errorMessage only)", () => {
  const text = formatCodeFailure({ success: false, errorMessage: "boom" });
  assert.ok(text.startsWith("Code execution failed: boom"));
  assert.doesNotMatch(text, /Hints:/);
});
