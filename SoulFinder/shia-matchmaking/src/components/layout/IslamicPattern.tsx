export function IslamicPattern({ className = "" }: { className?: string }) {
  return (
    <div
      className={`pointer-events-none absolute inset-0 islamic-pattern opacity-100 ${className}`}
      aria-hidden="true"
    />
  );
}
