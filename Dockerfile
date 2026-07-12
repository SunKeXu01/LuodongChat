FROM public.ecr.aws/docker/library/node:24-alpine AS build
WORKDIR /app
RUN corepack enable
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml tsconfig.json ./
RUN pnpm install --frozen-lockfile --ignore-scripts
COPY src ./src
COPY test ./test
RUN pnpm typecheck && pnpm build

FROM public.ecr.aws/docker/library/node:24-alpine AS runtime
ENV NODE_ENV=production
WORKDIR /app
RUN addgroup -S connector && adduser -S connector -G connector
COPY --from=build /app/dist ./dist
COPY --from=build /app/node_modules ./node_modules
COPY --from=build /app/package.json ./package.json
COPY db ./db
USER connector
EXPOSE 8787
CMD ["node", "dist/src/server.js"]
