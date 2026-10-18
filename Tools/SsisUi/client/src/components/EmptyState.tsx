export default function EmptyState({ message }: { message: string }) {
  return (
    <p className="rounded border border-dashed border-gray-300 bg-gray-50 px-3 py-4 text-sm italic text-gray-500">
      {message}
    </p>
  );
}
