import { test } from "node:test";
import assert from "node:assert/strict";

const { normalizeEnums } = await import("../build/framework/registry.js");

const schema = {
  type: "object",
  properties: {
    viewType: { type: "string", enum: ["floor_plan", "ceiling_plan", "3d"] },
    items: {
      type: "array",
      items: {
        type: "object",
        properties: { locationLine: { type: "string", enum: ["wallCenterline", "finishFaceInterior"] } },
      },
    },
    mode: { type: "string", enum: ["a_b", "ab"] },
  },
};

test("near-miss enum values are mapped to the canonical value", () => {
  const args = { viewType: "floorPlan", items: [{ locationLine: "finish_face_interior" }, { locationLine: "Wall Centerline" }] };
  normalizeEnums(args, schema);
  assert.equal(args.viewType, "floor_plan");
  assert.equal(args.items[0].locationLine, "finishFaceInterior");
  assert.equal(args.items[1].locationLine, "wallCenterline");
});

test("exact values, unknown values and ambiguous matches are left alone", () => {
  const args = { viewType: "3d", mode: "A-B" };
  normalizeEnums(args, schema);
  assert.equal(args.viewType, "3d");
  assert.equal(args.mode, "A-B"); // matches both "a_b" and "ab": leave it for Ajv to reject
  const other = { viewType: "section" };
  normalizeEnums(other, schema);
  assert.equal(other.viewType, "section");
});
