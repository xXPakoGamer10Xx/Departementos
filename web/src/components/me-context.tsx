"use client";
import { createContext, useContext } from "react";
import type { Me } from "@/lib/api";

export const MeContext = createContext<Me | null>(null);

export function useMe(): Me {
  const me = useContext(MeContext);
  if (!me) throw new Error("useMe debe usarse dentro del layout de la aplicación.");
  return me;
}
