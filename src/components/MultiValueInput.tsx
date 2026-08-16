import { KeyboardEvent, useId, useState } from "react";

interface MultiValueInputProps {
  label: string;
  description: string;
  values: string[];
  onChange: (values: string[]) => void;
  placeholder: string;
  maximumLength?: number;
}

export function normalizeMultiValue(value: string): string {
  return value
    .normalize("NFKC")
    .toLocaleLowerCase("ja-JP")
    .trim()
    .replace(/\s+/g, " ");
}

export function MultiValueInput({
  label,
  description,
  values,
  onChange,
  placeholder,
  maximumLength = 200,
}: MultiValueInputProps) {
  const inputId = useId();
  const [draft, setDraft] = useState("");
  const [message, setMessage] = useState<string | null>(null);

  const add = () => {
    const value = draft.trim();
    if (!value) {
      setMessage(`${label}を入力してください。`);
      return;
    }
    if (value.includes("\n") || value.includes("\r") || value.length > maximumLength) {
      setMessage(`${label}は${maximumLength}文字以内の1行で入力してください。`);
      return;
    }
    if (values.length >= 50) {
      setMessage(`${label}は50件まで登録できます。`);
      return;
    }
    const normalized = normalizeMultiValue(value);
    if (values.some((current) => normalizeMultiValue(current) === normalized)) {
      setMessage(`同じ${label}がすでに登録されています。`);
      return;
    }
    onChange([...values, value]);
    setDraft("");
    setMessage(null);
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key !== "Enter" || event.nativeEvent.isComposing) return;
    event.preventDefault();
    add();
  };

  return (
    <div className="multi-value-field">
      <div className="multi-value-heading">
        <label htmlFor={inputId}>{label}</label>
        <small>{description}</small>
      </div>
      <div className="multi-value-entry">
        <input
          id={inputId}
          type="text"
          value={draft}
          maxLength={maximumLength}
          placeholder={placeholder}
          aria-describedby={message ? `${inputId}-message` : undefined}
          aria-invalid={Boolean(message)}
          onChange={(event) => {
            setDraft(event.target.value);
            setMessage(null);
          }}
          onKeyDown={handleKeyDown}
        />
        <button type="button" className="button secondary" onClick={add}>追加</button>
      </div>
      {message && <small id={`${inputId}-message`} className="field-error-message" role="alert">{message}</small>}
      {values.length > 0 && (
        <ul className="multi-value-list" aria-label={`${label}の登録内容`}>
          {values.map((value, index) => (
            <li key={`${normalizeMultiValue(value)}-${index}`}>
              <span>{value}</span>
              <button
                type="button"
                aria-label={`${label}「${value}」を削除`}
                onClick={() => onChange(values.filter((_, current) => current !== index))}
              >
                削除
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
