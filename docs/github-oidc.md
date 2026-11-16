# GitHub Actions → AWS with OIDC

`deploy.yml` uses short-lived credentials from GitHub's OIDC provider, so there are no AWS access keys
stored in GitHub. Do this once per AWS account.

## 1. Add GitHub as an identity provider

IAM console → **Identity providers** → **Add provider**:

- Type: OpenID Connect
- Provider URL: `https://token.actions.githubusercontent.com`
- Audience: `sts.amazonaws.com`

## 2. Create the deploy role

Create a role with this **trust policy**. Replace `ACCOUNT_ID`, `OWNER` and `REPO`:

```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Principal": { "Federated": "arn:aws:iam::ACCOUNT_ID:oidc-provider/token.actions.githubusercontent.com" },
    "Action": "sts:AssumeRoleWithWebIdentity",
    "Condition": {
      "StringEquals": { "token.actions.githubusercontent.com:aud": "sts.amazonaws.com" },
      "StringLike": { "token.actions.githubusercontent.com:sub": "repo:OWNER/REPO:*" }
    }
  }]
}
```

For **permissions**, the role only needs to assume the roles that `cdk bootstrap` created:

```json
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Action": "sts:AssumeRole",
    "Resource": "arn:aws:iam::ACCOUNT_ID:role/cdk-*"
  }]
}
```

## 3. Configure the repository

- Secret `AWS_DEPLOY_ROLE_ARN`: the role's ARN
- Secret `OPS_EMAIL` (optional): receives ops pages, platform alarms and budget alerts. A secret, not a variable, so it is masked in the logs of a public repo
- Variables (optional): `AWS_REGION` (default `us-east-2`); `MONTHLY_BUDGET_USD` (budget alert amount, default 5, 0 = none); `DOMAIN_NAME` and `CERTIFICATE_ARN` for a custom domain (ACM certificate in us-east-1)
- Environments `dev` and `prod`: add required reviewers on `prod` for a manual approval gate

## 4. Deploy

Every push to `main` that passes CI deploys to `dev` automatically (the Deploy workflow runs on CI's `workflow_run`, checks out the exact commit CI tested, deploys, then smoke-tests the site, the API and the guest API). **Actions → Deploy → Run workflow** still deploys `dev` or `prod` on demand.

## If your AWS Organization blocks OIDC providers

Some organizations deny `iam:*OpenIDConnectProvider*` with a service control policy. Then use a deploy-only IAM user instead:

1. Create an IAM user (no console access) with one inline policy that only lets it assume the CDK bootstrap roles:
   ```json
   { "Version": "2012-10-17", "Statement": [{ "Effect": "Allow", "Action": "sts:AssumeRole",
     "Resource": "arn:aws:iam::ACCOUNT_ID:role/cdk-hnb659fds-*-ACCOUNT_ID-REGION" }] }
   ```
2. Create an access key and store it as the secrets `AWS_ACCESS_KEY_ID` and `AWS_SECRET_ACCESS_KEY` (pipe it straight into `gh secret set` so it is never printed).
3. Rotate the key periodically: create a second key, update the secrets, then delete the old key.

The workflow uses `AWS_DEPLOY_ROLE_ARN` when it is set and falls back to the key otherwise.
