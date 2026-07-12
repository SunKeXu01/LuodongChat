FROM public.ecr.aws/docker/library/node:24-alpine@sha256:a0b9bf06e4e6193cf7a0f58816cc935ff8c2a908f81e6f1a95432d679c54fbfd AS build
WORKDIR /app
RUN corepack enable
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml tsconfig.json tsconfig.build.json ./
RUN pnpm install --frozen-lockfile --ignore-scripts
COPY src ./src
COPY test ./test
RUN pnpm typecheck && pnpm build && pnpm prune --prod

FROM public.ecr.aws/docker/library/node:24-alpine@sha256:a0b9bf06e4e6193cf7a0f58816cc935ff8c2a908f81e6f1a95432d679c54fbfd AS runtime
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
