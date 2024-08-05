# OneDrive Simple Management Tool

![OneDrive-Simple-Management-Tool](https://socialify.git.ci/BSGZ123/OneDrive-Simple-Management-Tool/image?language=1&name=1&owner=1&theme=Light)

OneDrive简单管理工具，使用WinUI3开发

# 操作说明
...........................................


********


# 点点滴滴

## 当前
1. 简单UI
2. 下载
3. 上传


## 出现的问题
- [X] 文件列表导航工具栏未居右侧(2024-08-04)
应为RelativePanel.AlignRightWithPanel

- [X] 需要管理员权限启动应用(2024-08-04)
已确定问题，首先程序启动需获取(创建)身份令牌缓存文件，package打包部署时，文件的读写位置在常规权限无法覆盖的区域(疑似)。Unpackaged方式部署时，一切操作都在程序所在目录，即不再需要管理员权限读写特权目录了。

## 预期
- [X] 登录(2024-08-04)
- [ ] UI
- [X] 容量(2024-08-04)
- [ ] 下载
- [ ] 上传
- [ ] 下载进度
- [ ] 下载(多来源)
- [ ] 预览
- [ ] 共享
- [ ] 自动同步
- [ ] 重命名
- [ ] 删除
- [ ] 属性
- [ ] 转换常见格式文件(PDF)
- [ ] 新标签打开
- [ ] 自选主题
- [ ] 多账户
- [ ] 语言国际化
- [ ] 工具页 
