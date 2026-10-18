# Pruebas de extremo a extremo (Playwright)

Recorren la web de verdad, en Chrome, contra una pila completa (API + Worker + web + PostgreSQL):

| Archivo | Flujo |
|---|---|
| `claim.spec.ts` | El inquilino sube un comprobante → el propietario lo ve **sin recargar** (flujo en vivo), lo confirma → el inquilino recibe su aviso (empieza por su nombre). Una segunda confirmación se rechaza. |
| `cash.spec.ts` | Cobro en efectivo con el código del inquilino (una sola vez); el cobrador y el inquilino no alcanzan pantallas ajenas. |
| `reminder.spec.ts` | El worker avisa de una renta por vencer (una vez al día). |
| `business-data.spec.ts` | Importar Excel/CSV (vista previa, omitir fila mala, repetir no duplica), fiador, depósito en 3 abonos, depósito en custodia fuera del ingreso, el inquilino no entra a pantallas del personal. |

Cada prueba crea **su propia organización y personas** por la API, así que no dependen de datos de demostración ni entre sí.

## Cómo correrlas (todo en local)

```bash
docker compose up -d postgres      # una vez
./web/e2e/run.sh                   # todas
./web/e2e/run.sh claim.spec.ts     # solo una (acepta cualquier argumento de playwright)
```

`run.sh` crea la base temporal `habita_e2e`, levanta API (5334), worker (revisa cada 3 s) y web (3101) en puertos libres de uso normal, corre las pruebas con el Chrome del sistema (sin descargar navegadores) y al terminar lo apaga y borra la base. No guarda ni publica nada. Los registros de la corrida quedan en una carpeta temporal que el script imprime; los fallos dejan capturas y trazas en `web/e2e/.results/` (ignorada por git).

Nota: el worker corre durante las pruebas, así que además de los avisos que se prueban pueden llegar otros reales (p. ej. «cargo vencido» al personal); las pruebas buscan el aviso por su texto, no por la cuenta total.
