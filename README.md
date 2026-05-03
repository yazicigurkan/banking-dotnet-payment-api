# banking-dotnet-payment-api

Minimal uygulama repository ornegi. Pipeline logic burada tutulmaz; `.github/workflows/*`
dosyalari merkezi `bank-devsecops-pipeline-platform` reusable workflow'larini tag ile cagirir.

Deployment modelleri:

- `deployment_type: iis` icin Nexus artifact ve IIS PowerShell/WebDeploy akisi
- `deployment_type: kubernetes` icin Docker image, Twistlock, Harbor ve Argo CD GitOps akisi

## GitOps deployment

Kubernetes deployment'lari pipeline tarafindan cluster'a dogrudan Helm install/upgrade
yapmadan ilerler. Pipeline Docker image'i Harbor'a push eder, Twistlock sonucunu
dogrular ve ilgili branch'teki Helm values dosyasini gunceller:

- DEV: `deploy/k8s/values-dev.yaml`
- TEST: `deploy/k8s/values-test.yaml`
- PROD: `deploy/k8s/values-prod.yaml`

Argo CD Application manifestleri `deploy/argocd/` altindadir. Her ortam kendi branch
ve namespace eslesmesiyle calisir:

- `payment-api-dev`: `DEV` branch -> `payment-dev`
- `payment-api-test`: `TEST` branch -> `payment-test`
- `payment-api-prod`: `PROD` branch -> `payment-prod`

Pipeline'daki GitOps commit mesajlari `[skip ci]` icerir; bu sayede image tag
guncellemesi yeni bir CI/CD dongusu baslatmaz. PROD akisi yeniden build almaz,
TEST'te onaylanan immutable artifact/image bilgisini promote eder.
