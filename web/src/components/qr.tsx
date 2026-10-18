"use client";
import QRCode from "qrcode";
import jsQR from "jsqr";
import { useEffect, useEffectEvent, useRef, useState } from "react";
import { Skeleton } from "@/components/ui";

/** Draws a QR code for `value`. Generated in the browser: the code never goes to a third party. */
export function QrImage({ value, size = 224, label }: { value: string; size?: number; label: string }) {
  const [url, setUrl] = useState<{ value: string; src: string } | null>(null);
  useEffect(() => {
    let cancelled = false;
    QRCode.toDataURL(value, { width: size * 2, margin: 1, errorCorrectionLevel: "M" }).then((src) => { if (!cancelled) setUrl({ value, src }); }, () => {});
    return () => { cancelled = true; };
  }, [value, size]);
  if (url?.value !== value) return <Skeleton className="rounded-md" />;
  // eslint-disable-next-line @next/next/no-img-element -- a data: URL made in the browser; next/image cannot optimize it
  return <img src={url.src} alt={label} width={size} height={size} className="rounded-md border border-rule bg-white p-2" />;
}

/**
 * Reads QR codes with the rear camera. `onCode` fires once per distinct text (a scanner pointed at the same code keeps seeing it).
 * The camera is only opened while this component is mounted, and every track is stopped when it leaves.
 */
export function QrScanner({ onCode, onProblem }: { onCode: (text: string) => void; onProblem: (message: string) => void }) {
  const video = useRef<HTMLVideoElement>(null);
  const handleCode = useEffectEvent(onCode);
  const handleProblem = useEffectEvent(onProblem);

  useEffect(() => {
    if (!navigator.mediaDevices?.getUserMedia) { handleProblem("Este navegador no puede usar la cámara. Escribe el código a mano."); return; }
    let stopped = false; let frame = 0; let last = "";
    let stream: MediaStream | null = null;
    const canvas = document.createElement("canvas");
    const ctx = canvas.getContext("2d", { willReadFrequently: true });

    function tick() {
      const v = video.current;
      if (stopped || !v || !ctx) return;
      if (v.readyState === v.HAVE_ENOUGH_DATA && v.videoWidth > 0) {
        canvas.width = v.videoWidth; canvas.height = v.videoHeight;
        ctx.drawImage(v, 0, 0, canvas.width, canvas.height);
        const found = jsQR(ctx.getImageData(0, 0, canvas.width, canvas.height).data, canvas.width, canvas.height, { inversionAttempts: "dontInvert" });
        if (found?.data && found.data !== last) { last = found.data; handleCode(found.data); }
      }
      frame = requestAnimationFrame(tick);
    }

    navigator.mediaDevices.getUserMedia({ video: { facingMode: { ideal: "environment" } }, audio: false }).then((s) => {
      if (stopped) { s.getTracks().forEach((t) => t.stop()); return; }
      stream = s;
      if (video.current) { video.current.srcObject = s; video.current.play().catch(() => {}); }
      frame = requestAnimationFrame(tick);
    }, (e: DOMException) => {
      handleProblem(e.name === "NotAllowedError" ? "No diste permiso para usar la cámara. Actívalo en el navegador o escribe el código a mano." : "No se pudo abrir la cámara. Escribe el código a mano.");
    });

    return () => { stopped = true; cancelAnimationFrame(frame); stream?.getTracks().forEach((t) => t.stop()); };
  }, []);

  return (
    <div className="relative overflow-hidden rounded-md border border-rule bg-ink">
      <video ref={video} muted playsInline aria-label="Vista de la cámara para escanear el código QR" className="aspect-square w-full object-cover" />
      <div aria-hidden className="pointer-events-none absolute inset-[18%] rounded-lg border-2 border-white/80" />
    </div>
  );
}
