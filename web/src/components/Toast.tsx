import { useEffect } from 'react';

export interface ToastMessage {
  kind: 'success' | 'error';
  text: string;
}

export function Toast({ message, onDone }: { message: ToastMessage | null; onDone(): void }) {
  useEffect(() => {
    if (!message) return;
    const t = setTimeout(onDone, message.kind === 'error' ? 5000 : 2500);
    return () => clearTimeout(t);
  }, [message, onDone]);

  if (!message) return null;
  return (
    <div className={`toast toast-${message.kind}`} role="status" aria-live="polite">
      {message.text}
    </div>
  );
}
