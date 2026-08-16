import { fireEvent, render, screen } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it } from "vitest";
import { MultiValueInput, normalizeMultiValue } from "./MultiValueInput";

function Fixture() {
  const [values, setValues] = useState<string[]>([]);
  return (
    <MultiValueInput
      label="タグ"
      description="検索の観点"
      values={values}
      onChange={setValues}
      placeholder="例：Windows"
    />
  );
}

describe("MultiValueInput", () => {
  it("normalizes width, case, and whitespace for duplicate detection", () => {
    expect(normalizeMultiValue(" ＷＩＮＤＯＷＳ　11 ")).toBe("windows 11");
  });

  it("adds and removes values and rejects a normalized duplicate", () => {
    render(<Fixture />);
    const input = screen.getByLabelText("タグ");
    fireEvent.change(input, { target: { value: "Windows" } });
    fireEvent.keyDown(input, { key: "Enter" });
    expect(screen.getByText("Windows")).toBeVisible();

    fireEvent.change(input, { target: { value: "ＷＩＮＤＯＷＳ" } });
    fireEvent.click(screen.getByRole("button", { name: "追加" }));
    expect(screen.getByRole("alert")).toHaveTextContent("同じタグがすでに登録されています");
    expect(screen.getAllByRole("listitem")).toHaveLength(1);

    fireEvent.click(screen.getByRole("button", { name: "タグ「Windows」を削除" }));
    expect(screen.queryByText("Windows")).not.toBeInTheDocument();
  });
});
