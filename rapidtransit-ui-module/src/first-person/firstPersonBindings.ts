import { bindValue } from "cs2/api";

export interface FirstPersonData {
  active: boolean;
  followedEntity: string;
  vehicle: string;
  stop: { dwellMinutes: number; plannedDepartureMinute: number | null } | null;
  signal: FirstPersonSignalData | null;
}

export interface FirstPersonSignalData {
  status: string;
  movement: string;
  distanceMeters: number;
  streetName: string;
  firstLight: string;
  secondLight: string;
  thirdLight: string;
}

export const dataJson$ = bindValue<string>("ATOFirstPerson", "dataJson");
export const place$ = bindValue<string>("ATOFirstPerson", "place");

export function parseData(json: string): FirstPersonData | null {
  if (!json) return null;
  try { return JSON.parse(json) as FirstPersonData; } catch { return null; }
}
