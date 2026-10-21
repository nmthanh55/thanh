FROM node:22-slim
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --omit=dev
COPY src ./src
COPY public ./public
ENV HOST=0.0.0.0 PORT=3000
EXPOSE 3000
CMD ["node", "src/server.js"]
