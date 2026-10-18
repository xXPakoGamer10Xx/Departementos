/** Mirrors the server's CLABE check so the form can say what is wrong before sending. The server still decides. */
export function normalizeClabe(input: string): string {
  return input.replace(/[\s-]/g, "");
}

export function isValidClabe(input: string): boolean {
  const c = normalizeClabe(input);
  if (!/^\d{18}$/.test(c)) return false;
  const weights = [3, 7, 1];
  const sum = [...c.slice(0, 17)].reduce((acc, d, i) => acc + ((Number(d) * weights[i % 3]) % 10), 0);
  return (10 - (sum % 10)) % 10 === Number(c[17]);
}
