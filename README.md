# banking-dotnet-payment-api

Minimal uygulama repository ornegi. Pipeline logic burada tutulmaz; `.github/workflows/*`
dosyalari merkezi `bank-devsecops-pipeline-platform` reusable workflow'larini tag ile cagirir.

Deployment modelleri:

- `deployment_type: iis` icin Nexus artifact ve IIS PowerShell/WebDeploy akisi
- `deployment_type: kubernetes` icin Docker image, Twistlock, Harbor ve Helm akisi
